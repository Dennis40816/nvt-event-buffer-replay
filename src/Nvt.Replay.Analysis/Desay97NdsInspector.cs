using Nvt.Replay.Core;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Analysis;

public sealed record Desay97InspectionReport(
    string SourcePath,
    string SourceSha256,
    Desay97Profile Profile,
    uint EventBufferBase,
    IReadOnlyList<Desay97Frame> Frames,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);

public sealed class Desay97NdsInspector
{
    public async Task<Desay97InspectionReport> InspectAsync(
        string path,
        Desay97Profile profile,
        uint eventBufferBase = 0x99000,
        CancellationToken cancellationToken = default)
    {
        var session = await NdsCaptureSession.LoadAsync(path, cancellationToken: cancellationToken);
        return session.DecodeDesay97(profile, eventBufferBase);
    }
}
