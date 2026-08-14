using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Analysis;

public sealed record CommonInspectionReport(
    string SourcePath,
    string SourceSha256,
    CommonEventBufferVersion Version,
    IReadOnlyList<CommonEventBufferFrame> Frames,
    IReadOnlyList<Nvt.Replay.Core.ReplayDiagnostic> Diagnostics);

public sealed class CommonNdsInspector
{
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
