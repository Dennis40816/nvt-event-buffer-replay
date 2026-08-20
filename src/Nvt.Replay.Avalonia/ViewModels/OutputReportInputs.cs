using System.Collections.ObjectModel;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>
/// Immutable, operation-scoped inputs captured on the UI thread before analysis is scheduled.
/// The capture, replay, and frame cache are immutable build results retained by reference; mutable
/// human-review state and diagnostic detail maps are copied so background analysis cannot observe
/// a mixture of two UI revisions.
/// </summary>
internal sealed record OutputReportInputs(
    CaptureSession Capture,
    ITouchReplaySession Replay,
    ReplayDecodeConfiguration DecodeConfiguration,
    EvidenceStatus Evidence,
    AnalysisRange Range,
    IReadOnlyList<ITouchReplaySnapshot> Snapshots,
    IReadOnlyList<ReplayDiagnostic> Diagnostics,
    ReviewSession Review,
    long ReportRevision)
{
    public static OutputReportInputs CaptureCurrent(
        CaptureSession capture,
        ITouchReplaySession replay,
        ReplayDecodeConfiguration decodeConfiguration,
        EvidenceStatus evidence,
        AnalysisRange range,
        ReplayFrameCache? frameCache,
        ReviewInspectorWorkspace reviewWorkspace)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(decodeConfiguration);
        ArgumentNullException.ThrowIfNull(range);
        ArgumentNullException.ThrowIfNull(reviewWorkspace);
        var snapshots = frameCache?.Snapshots ?? reviewWorkspace.Frames;
        if (snapshots.Count != replay.Count)
            throw new ArgumentException("Frame cache must belong to the captured replay.", nameof(frameCache));

        var diagnostics = reviewWorkspace.ReviewSession.Diagnostics.Select(FreezeDiagnostic).ToArray();
        var logicalIndexBySourceId = BuildLogicalIndex(snapshots);
        var review = new ReviewSession(
            diagnostics,
            logicalIndexBySourceId,
            reviewWorkspace.ReviewOptions);
        review.ImportState(reviewWorkspace.ReviewSession.ExportState().ToArray());
        return new OutputReportInputs(
            capture,
            replay,
            decodeConfiguration,
            evidence,
            range,
            snapshots,
            diagnostics,
            review,
            reviewWorkspace.ReportRevision);
    }

    public CaptureAnalysisReport Build(CancellationToken cancellationToken = default) =>
        new CaptureAnalyzer().Analyze(
            Capture.SourcePath,
            Capture.SourceSha256,
            DecodeConfiguration,
            Evidence,
            Replay,
            Diagnostics,
            Review,
            Range,
            cancellationToken: cancellationToken,
            snapshots: Snapshots);

    private static ReplayDiagnostic FreezeDiagnostic(ReplayDiagnostic source)
    {
        var details = source.Details is null
            ? null
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(source.Details, StringComparer.Ordinal));
        return source with { Details = details };
    }

    private static IReadOnlyDictionary<string, int> BuildLogicalIndex(
        IReadOnlyList<ITouchReplaySnapshot> frames)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var logicalIndex = 0; logicalIndex < frames.Count; logicalIndex++)
        {
            var snapshot = frames[logicalIndex];
            foreach (var source in snapshot.PhysicalRecords) result[source.StableId] = logicalIndex;
            result[snapshot.PrimarySource.StableId] = logicalIndex;
        }
        return result;
    }
}
