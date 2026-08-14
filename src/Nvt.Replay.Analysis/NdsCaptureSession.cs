using System.Globalization;
using System.Security.Cryptography;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

public sealed record CaptureLoadProgress(string Phase, int RecordsRead);

public sealed class NdsCaptureSession
{
    private NdsCaptureSession(
        string sourcePath,
        string sourceSha256,
        SourceProbeResult probe,
        IReadOnlyList<SourceRecord> records)
    {
        SourcePath = sourcePath;
        SourceSha256 = sourceSha256;
        Probe = probe;
        Records = records;
    }

    public string SourcePath { get; }

    public string SourceSha256 { get; }

    public SourceProbeResult Probe { get; }

    public IReadOnlyList<SourceRecord> Records { get; }

    public static async Task<NdsCaptureSession> LoadAsync(
        string path,
        IProgress<CaptureLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var adapter = new NdsCommunicationLogAdapter();
        progress?.Report(new CaptureLoadProgress("Probing source", 0));
        var probe = await adapter.ProbeAsync(fullPath, cancellationToken);
        if (probe.Confidence == ProbeConfidence.None)
        {
            throw new InvalidDataException("The selected file does not match the NDS communication-log grammar.");
        }

        progress?.Report(new CaptureLoadProgress("Hashing source", 0));
        var sourceHash = await ComputeSha256Async(fullPath, cancellationToken);
        var records = new List<SourceRecord>();
        progress?.Report(new CaptureLoadProgress("Indexing records", 0));
        await foreach (var record in adapter.ReadAsync(
            new SourceOpenContext(fullPath, sourceHash),
            cancellationToken))
        {
            records.Add(record);
            if (records.Count % 100 == 0)
            {
                progress?.Report(new CaptureLoadProgress("Indexing records", records.Count));
            }
        }

        progress?.Report(new CaptureLoadProgress("Ready for configuration", records.Count));
        return new NdsCaptureSession(fullPath, sourceHash, probe, records);
    }

    public CommonInspectionReport DecodeCommon(CommonEventBufferVersion version)
    {
        var decoder = new CommonEventBufferDecoder();
        var monitor = new CommonStreamMonitor();
        var frames = new List<CommonEventBufferFrame>();
        var diagnostics = new List<ReplayDiagnostic>();

        foreach (var record in Records)
        {
            if (record.Operation != BusOperation.Paint || record.Address != 0x01)
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
                "No NDS Paint TP 0x01 Common event-buffer records were decoded.",
                SourceSha256,
                new SourceLocation(0, 0)));
        }

        return new CommonInspectionReport(SourcePath, SourceSha256, version, frames, diagnostics);
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

    private static ReplayDiagnostic NewDiagnostic(
        SourceRecord source,
        DiagnosticSeverity severity,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(severity, code, message, source.StableId, source.Location, details);
}
