using System.Globalization;
using System.Security.Cryptography;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

public sealed record CaptureLoadProgress(string Phase, int RecordsRead);

public sealed class CaptureSession
{
    private CaptureSession(
        string sourcePath,
        string sourceSha256,
        SourceProbeResult probe,
        IReadOnlyList<SourceRecord> records,
        IReadOnlyList<ReplayDiagnostic> transportDiagnostics,
        RegisterAnnotationIndex registerAnnotationIndex,
        RegisterAnnotationProjection registerAnnotations)
    {
        SourcePath = sourcePath;
        SourceSha256 = sourceSha256;
        Probe = probe;
        Records = records;
        TransportDiagnostics = transportDiagnostics;
        RegisterAnnotationIndex = registerAnnotationIndex;
        RegisterAnnotations = registerAnnotations;
        RegisterProfile = registerAnnotations.Profile?.IcFamily;
    }

    public string SourcePath { get; }

    public string SourceSha256 { get; }

    public SourceProbeResult Probe { get; }

    public IReadOnlyList<SourceRecord> Records { get; }

    public IReadOnlyList<ReplayDiagnostic> TransportDiagnostics { get; }

    public IReadOnlyList<ReplayDiagnostic> SourceDiagnostics => TransportDiagnostics;

    public IReadOnlyList<ReplayDiagnostic> RegisterDiagnostics => RegisterAnnotations.Diagnostics;

    public RegisterAnnotationIndex RegisterAnnotationIndex { get; }

    public RegisterAnnotationProjection RegisterAnnotations { get; }

    public string? RegisterProfile { get; }

    public static async Task<CaptureSession> LoadAsync(
        string path,
        IProgress<CaptureLoadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? adapterId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        progress?.Report(new CaptureLoadProgress("Probing source", 0));
        var probes = new List<(ISourceAdapter Adapter, SourceProbeResult Probe)>();
        foreach (var candidate in BuiltInSources.All)
        {
            probes.Add((candidate, await candidate.ProbeAsync(fullPath, cancellationToken)));
        }
        var selected = SelectAdapter(probes, adapterId);
        var adapter = selected.Adapter;
        var probe = selected.Probe;

        progress?.Report(new CaptureLoadProgress("Hashing source", 0));
        var sourceHash = await ComputeSha256Async(fullPath, cancellationToken);
        var records = new List<SourceRecord>();
        var sourceDiagnostics = new List<ReplayDiagnostic>();
        progress?.Report(new CaptureLoadProgress("Indexing records", 0));
        await foreach (var record in adapter.ReadAsync(
            new SourceOpenContext(fullPath, sourceHash, sourceDiagnostics.Add),
            cancellationToken))
        {
            records.Add(record);
            if (records.Count % 100 == 0)
            {
                progress?.Report(new CaptureLoadProgress("Indexing records", records.Count));
            }
        }

        progress?.Report(new CaptureLoadProgress("Ready for configuration", records.Count));
        var recordIndex = records.ToArray();
        var registerAnnotationIndex = new RegisterAnnotationIndex(recordIndex);
        var registerAnnotations = registerAnnotationIndex.Project(null, cancellationToken);
        return new CaptureSession(
            fullPath,
            sourceHash,
            probe,
            recordIndex,
            sourceDiagnostics.ToArray(),
            registerAnnotationIndex,
            registerAnnotations);
    }

    public CaptureSession WithRegisterProfile(
        string? icFamily,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var profile = NvtRegisterCatalog.FindProfile(icFamily);
        if (!string.IsNullOrWhiteSpace(icFamily) && profile is null)
            throw new ArgumentException($"Unknown NVT register profile '{icFamily}'.", nameof(icFamily));
        var identity = profile?.Identity ?? NvtRegisterCatalog.AutomaticProfileIdentity;
        if (RegisterAnnotations.ProfileIdentity == identity) return this;
        var projection = RegisterAnnotationIndex.Project(profile, cancellationToken);
        return new CaptureSession(
            SourcePath,
            SourceSha256,
            Probe,
            Records,
            TransportDiagnostics,
            RegisterAnnotationIndex,
            projection);
    }

    public CommonInspectionReport DecodeCommon(
        CommonEventBufferVersion version,
        CancellationToken cancellationToken = default) =>
        DecodeCommon(version, 0x01, cancellationToken);

    public CommonInspectionReport DecodeCommon(
        CommonEventBufferVersion version,
        int targetI2cAddress,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetI2cAddress(targetI2cAddress);
        var decoder = new CommonEventBufferDecoder();
        var monitor = new CommonStreamMonitor();
        var frames = new List<CommonEventBufferFrame>();
        var diagnostics = new List<ReplayDiagnostic>(TransportDiagnostics);
        diagnostics.AddRange(RegisterDiagnostics);

        foreach (var record in Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isNdsPaint = targetI2cAddress == 0x01 &&
                             record.Operation == BusOperation.Paint &&
                             record.Address == 0x01;
            var isDecodedI2cRead = record.Operation == BusOperation.Read &&
                                   IsEventBufferRead(record, targetI2cAddress);
            if (!isNdsPaint && !isDecodedI2cRead)
            {
                continue;
            }

            if (isNdsPaint && !record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(NewDiagnostic(
                    record,
                    DiagnosticSeverity.Warning,
                    "NDS_TARGET_MISMATCH",
                    $"NDS event record target is '{record.Target}', expected 'TP'."));
            }

            if (isNdsPaint &&
                (record.DeclaredByteCount != CommonEventBufferDecoder.FrameLength ||
                 record.Data.Count != CommonEventBufferDecoder.FrameLength))
            {
                diagnostics.Add(NewDiagnostic(
                    record,
                    DiagnosticSeverity.Error,
                    "NDS_BYTE_COUNT_MISMATCH",
                    $"NDS Common event record declares {record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} bytes and contains {record.Data.Count}; expected 80.",
                    new Dictionary<string, string>
                    {
                        ["declared"] = record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
                        ["actual"] = record.Data.Count.ToString(CultureInfo.InvariantCulture),
                        ["expected"] = CommonEventBufferDecoder.FrameLength.ToString(CultureInfo.InvariantCulture),
                    }));
                continue;
            }

            var decoded = decoder.Decode(record, version);
            diagnostics.AddRange(decoded.Diagnostics);
            if (decoded.Frame is not { } frame)
            {
                continue;
            }

            frames.Add(frame);
            if (frame.HostStateEligible)
            {
                diagnostics.AddRange(monitor.Observe(frame));
            }
        }

        if (frames.Count == 0)
        {
            diagnostics.Add(new ReplayDiagnostic(
                DiagnosticSeverity.Warning,
                "NO_EVENT_BUFFER_RECORDS",
                $"No Common event-buffer records were decoded for 7-bit I2C address 0x{targetI2cAddress:X2} " +
                "(expected NDS Paint TP 0x01, a profile-matched Event Buffer read, or an offset-only I2C read at register 0x00).",
                SourceSha256,
                new SourceLocation(0, 0)));
        }

        return new CommonInspectionReport(SourcePath, SourceSha256, version, frames, diagnostics);
    }

    private bool IsEventBufferRead(SourceRecord record, int targetI2cAddress)
    {
        if (record.Data.Count < CommonEventBufferDecoder.FrameLength)
        {
            return false;
        }

        if (record.I2c is { } i2c)
        {
            if (i2c.SlaveAddress != targetI2cAddress) return false;
        }
        else if (targetI2cAddress != 0x01 ||
                 !record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (record.Address is null &&
            record.SourceFields?.GetValueOrDefault("register_page_known") == "false" &&
            record.SourceFields.GetValueOrDefault("register_offset")?.Equals("0x00", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return RegisterAnnotations.TryGet(record, out var annotation) &&
               annotation.Description.Region == "Event Buffer" &&
               annotation.Description.Offset == 0;
    }

    public Desay97InspectionReport DecodeDesay97(
        Desay97Profile profile,
        uint? eventBufferBase = null,
        CancellationToken cancellationToken = default) =>
        DecodeDesay97(profile, eventBufferBase, 0x01, cancellationToken);

    public Desay97InspectionReport DecodeDesay97(
        Desay97Profile profile,
        uint? eventBufferBase,
        int targetI2cAddress,
        CancellationToken cancellationToken = default)
    {
        ValidateTargetI2cAddress(targetI2cAddress);
        var resolvedEventBufferBase = eventBufferBase ??
            NvtRegisterCatalog.FindProfile(RegisterProfile)?.EventBufferBase ??
            0x99000;
        var assembly = new Desay97Assembler(resolvedEventBufferBase, targetI2cAddress)
            .Assemble(Records, cancellationToken);
        var decoder = new Desay97Decoder();
        var frames = new List<Desay97Frame>();
        var diagnostics = new List<ReplayDiagnostic>(TransportDiagnostics);
        diagnostics.AddRange(RegisterDiagnostics);
        diagnostics.AddRange(assembly.Diagnostics);
        foreach (var packet in assembly.Packets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decoded = decoder.Decode(packet, profile);
            diagnostics.AddRange(decoded.Diagnostics);
            if (decoded.Frame is { } frame)
            {
                frames.Add(frame);
            }
        }

        return new Desay97InspectionReport(
            SourcePath,
            SourceSha256,
            profile,
            resolvedEventBufferBase,
            frames,
            diagnostics);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(digest);
    }

    private static (ISourceAdapter Adapter, SourceProbeResult Probe) SelectAdapter(
        IReadOnlyList<(ISourceAdapter Adapter, SourceProbeResult Probe)> probes,
        string? adapterId)
    {
        if (!string.IsNullOrWhiteSpace(adapterId))
        {
            var explicitChoice = probes.FirstOrDefault(item => item.Adapter.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
            if (explicitChoice.Adapter is null)
                throw new ArgumentException($"Unknown source adapter '{adapterId}'.", nameof(adapterId));
            if (explicitChoice.Probe.Confidence == ProbeConfidence.None)
                throw new InvalidDataException($"The file does not match explicitly selected adapter '{explicitChoice.Adapter.DisplayName}'.");
            return explicitChoice;
        }

        var matches = probes.Where(item => item.Probe.Confidence != ProbeConfidence.None).ToArray();
        if (matches.Length == 0)
            throw new InvalidDataException("The selected file does not match any supported capture-source schema. Run 'nvt-replay probe <file>' for details.");
        var highest = matches.Max(item => item.Probe.Confidence);
        var finalists = matches.Where(item => item.Probe.Confidence == highest).ToArray();
        if (finalists.Length != 1)
            throw new SourceSelectionRequiredException(finalists.Select(item => item.Probe).ToArray());
        return finalists[0];
    }

    private static ReplayDiagnostic NewDiagnostic(
        SourceRecord source,
        DiagnosticSeverity severity,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(severity, code, message, source.StableId, source.Location, details);

    private static void ValidateTargetI2cAddress(int targetI2cAddress)
    {
        if (targetI2cAddress is < 0 or > 0x7F)
            throw new ArgumentOutOfRangeException(
                nameof(targetI2cAddress),
                targetI2cAddress,
                "I2C slave address must be a 7-bit value from 0x00 through 0x7F.");
    }
}

public sealed class SourceSelectionRequiredException(IReadOnlyList<SourceProbeResult> candidates)
    : Exception($"Source format is ambiguous; select one adapter explicitly: {string.Join(", ", candidates.Select(item => item.AdapterId))}")
{
    public IReadOnlyList<SourceProbeResult> Candidates { get; } = candidates;
}
