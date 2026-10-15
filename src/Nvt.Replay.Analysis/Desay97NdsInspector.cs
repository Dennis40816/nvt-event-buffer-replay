using Nvt.Replay.Core;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Analysis;

/// <summary>Holds source provenance, decoding configuration, frames, and diagnostics for a Desay97 inspection.</summary>
/// <param name="SourcePath">The path of the inspected capture.</param>
/// <param name="SourceSha256">The SHA-256 hash of the inspected capture.</param>
/// <param name="Profile">The profile used to decode Desay97 packets.</param>
/// <param name="EventBufferBase">The event buffer base address used to assemble Desay97 packets.</param>
/// <param name="Frames">The Desay97 frames decoded from the capture.</param>
/// <param name="Diagnostics">The transport, register, assembly, and decoding diagnostics.</param>
public sealed record Desay97InspectionReport(
    string SourcePath,
    string SourceSha256,
    Desay97Profile Profile,
    uint EventBufferBase,
    IReadOnlyList<Desay97Frame> Frames,
    IReadOnlyList<ReplayDiagnostic> Diagnostics) : IFormatInspectionReport;

/// <summary>Loads capture files and decodes their Desay97 event buffers.</summary>
public sealed class Desay97NdsInspector
{
    /// <summary>Loads a capture asynchronously and decodes Desay97 frames using the supplied profile and event buffer base.</summary>
    public async Task<Desay97InspectionReport> InspectAsync(
        string path,
        Desay97Profile profile,
        uint eventBufferBase = 0x99000,
        string? sourceAdapterId = null,
        CancellationToken cancellationToken = default)
    {
        var session = await CaptureSession.LoadAsync(path, cancellationToken: cancellationToken, adapterId: sourceAdapterId);
        return session.DecodeDesay97(profile, eventBufferBase);
    }
}
