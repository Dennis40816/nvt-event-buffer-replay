using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Analysis;

/// <summary>Holds source identity, the selected Common event-buffer version, decoded frames, and inspection diagnostics.</summary>
/// <param name="SourcePath">The path of the inspected capture source.</param>
/// <param name="SourceSha256">The SHA-256 hash of the inspected source file.</param>
/// <param name="Version">The Common event-buffer version used to decode the capture.</param>
/// <param name="Frames">The Common event-buffer frames decoded from the capture.</param>
/// <param name="Diagnostics">The diagnostics collected while loading and decoding the capture.</param>
public sealed record CommonInspectionReport(
    string SourcePath,
    string SourceSha256,
    CommonEventBufferVersion Version,
    IReadOnlyList<CommonEventBufferFrame> Frames,
    IReadOnlyList<Nvt.Replay.Core.ReplayDiagnostic> Diagnostics) : IFormatInspectionReport;

/// <summary>Loads captures and produces Common event-buffer inspection reports for the selected version.</summary>
public sealed class CommonNdsInspector
{
    /// <summary>Loads a capture asynchronously and decodes its Common event-buffer records using the selected version.</summary>
    public async Task<CommonInspectionReport> InspectAsync(
        string path,
        CommonEventBufferVersion version,
        string? sourceAdapterId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await CaptureSession.LoadAsync(path, cancellationToken: cancellationToken, adapterId: sourceAdapterId);
        return session.DecodeCommon(version);
    }
}
