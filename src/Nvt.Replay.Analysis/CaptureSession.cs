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
        IReadOnlyList<ReplayDiagnostic> sourceDiagnostics)
    {
        SourcePath = sourcePath;
        SourceSha256 = sourceSha256;
        Probe = probe;
        Records = records;
        SourceDiagnostics = sourceDiagnostics;
    }

    public string SourcePath { get; }

    public string SourceSha256 { get; }

    public SourceProbeResult Probe { get; }

    public IReadOnlyList<SourceRecord> Records { get; }

    public IReadOnlyList<ReplayDiagnostic> SourceDiagnostics { get; }

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

        sourceDiagnostics.AddRange(new NvtRegisterActivityMonitor().Observe(records));
        progress?.Report(new CaptureLoadProgress("Ready for configuration", records.Count));
        return new CaptureSession(fullPath, sourceHash, probe, records, sourceDiagnostics);
    }

    public CommonInspectionReport DecodeCommon(CommonEventBufferVersion version)
    {
        var decoder = new CommonEventBufferDecoder();
        var monitor = new CommonStreamMonitor();
        var frames = new List<CommonEventBufferFrame>();
        var diagnostics = new List<ReplayDiagnostic>(SourceDiagnostics);

        foreach (var record in Records)
        {
            var isNdsPaint = record.Operation == BusOperation.Paint && record.Address == 0x01;
            var isDecodedI2cRead = record.Operation == BusOperation.Read && record.Address == 0x99000;
            if (!isNdsPaint && !isDecodedI2cRead)
            {
                continue;
            }

            if (!record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(NewDiagnostic(
                    record,
                    DiagnosticSeverity.Warning,
                    "NDS_TARGET_MISMATCH",
                    $"NDS event record target is '{record.Target}', expected 'TP'."));
            }

            if (record.DeclaredByteCount != CommonEventBufferDecoder.FrameLength ||
                record.Data.Count != CommonEventBufferDecoder.FrameLength)
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
                "No Common event-buffer records were decoded (expected NDS Paint TP 0x01 or I2C read at 0x99000).",
                SourceSha256,
                new SourceLocation(0, 0)));
        }

        return new CommonInspectionReport(SourcePath, SourceSha256, version, frames, diagnostics);
    }

    public Desay97InspectionReport DecodeDesay97(Desay97Profile profile, uint eventBufferBase = 0x99000)
    {
        var assembly = new Desay97Assembler(eventBufferBase).Assemble(Records);
        var decoder = new Desay97Decoder();
        var frames = new List<Desay97Frame>();
        var diagnostics = new List<ReplayDiagnostic>(SourceDiagnostics);
        diagnostics.AddRange(assembly.Diagnostics);
        foreach (var packet in assembly.Packets)
        {
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
            eventBufferBase,
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
}

public sealed class SourceSelectionRequiredException(IReadOnlyList<SourceProbeResult> candidates)
    : Exception($"Source format is ambiguous; select one adapter explicitly: {string.Join(", ", candidates.Select(item => item.AdapterId))}")
{
    public IReadOnlyList<SourceProbeResult> Candidates { get; } = candidates;
}
