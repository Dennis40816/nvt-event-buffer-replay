using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public enum ReplayRenderMode
{
    HostState,
    ReportedFrame,
    Compare,
}

public sealed record ReplayExtent(double MaximumX, double MaximumY)
{
    public static ReplayExtent Measure(IEnumerable<ReplayContact> contacts)
    {
        var captured = contacts.ToArray();
        return new ReplayExtent(
            Nice(captured.Select(item => (double)item.X).DefaultIfEmpty(1920).Max(), 1920),
            Nice(captured.Select(item => (double)item.Y).DefaultIfEmpty(1080).Max(), 1080));
    }

    private static double Nice(double maximum, double minimum)
    {
        var required = Math.Max(minimum, maximum * 1.08);
        return Math.Ceiling(required / 256) * 256;
    }
}

public sealed record ReplayScene(
    int LogicalIndex,
    int TotalFrames,
    ReplayTimelineEntry Timeline,
    IReadOnlyList<ReplayContact> ReportedContacts,
    IReadOnlyList<ReplayContact> HostContacts,
    bool GlobalPalm,
    ReplayExtent Extent,
    IReadOnlyList<ReplayDiagnostic> Diagnostics,
    IReadOnlyList<string> MarkerLabels);

public static class ReplaySceneFactory
{
    public static ReplayScene Create(
        ITouchReplaySnapshot snapshot,
        int totalFrames,
        ReplayExtent extent,
        IEnumerable<ReplayDiagnostic>? diagnostics = null,
        IEnumerable<ReplayMarker>? markers = null)
    {
        var sourceIds = snapshot.PhysicalRecords.Append(snapshot.PrimarySource)
            .Select(item => item.StableId)
            .ToHashSet(StringComparer.Ordinal);
        return new ReplayScene(
            snapshot.LogicalIndex,
            totalFrames,
            snapshot.Timeline,
            snapshot.ReportedContacts,
            snapshot.HostContacts,
            snapshot.GlobalPalm,
            extent,
            diagnostics?.Where(item => sourceIds.Contains(item.SourceRecordId)).ToArray() ?? [],
            markers?.Where(item => item.StartLogicalIndex <= snapshot.LogicalIndex && item.EndLogicalIndex >= snapshot.LogicalIndex)
                .Select(item => item.Label).ToArray() ?? []);
    }
}
