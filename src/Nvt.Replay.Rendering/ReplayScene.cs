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
    IReadOnlyList<ReplayContactTrail> ContactTrails,
    IReadOnlyList<ReplayDiagnostic> Diagnostics,
    IReadOnlyList<string> MarkerLabels);

public sealed record ReplayTrailPoint(ushort X, ushort Y, TouchStatus Status, int LogicalIndex);

public sealed record ReplayContactTrail(byte Id, TouchType Type, IReadOnlyList<ReplayTrailPoint> Points);

public static class ReplaySceneFactory
{
    public static ReplayScene Create(
        ITouchReplaySnapshot snapshot,
        int totalFrames,
        ReplayExtent extent,
        IEnumerable<ReplayDiagnostic>? diagnostics = null,
        IEnumerable<ReplayMarker>? markers = null,
        IReadOnlyList<ReplayContactTrail>? contactTrails = null)
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
            contactTrails ?? [],
            diagnostics?.Where(item => sourceIds.Contains(item.SourceRecordId)).ToArray() ?? [],
            markers?.Where(item => item.StartLogicalIndex <= snapshot.LogicalIndex && item.EndLogicalIndex >= snapshot.LogicalIndex)
                .Select(item => item.Label).ToArray() ?? []);
    }

    public static IReadOnlyList<ReplayContactTrail> BuildTrails(
        ITouchReplaySession replay,
        int logicalIndex,
        int maximumPoints = 8)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (logicalIndex < 0 || logicalIndex >= replay.Count)
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        if (maximumPoints < 2)
            throw new ArgumentOutOfRangeException(nameof(maximumPoints));

        var current = replay.Seek(logicalIndex);
        var trails = new List<ReplayContactTrail>();
        foreach (var contact in current.ReportedContacts.Where(IsTrackable).OrderBy(item => item.Id))
        {
            var points = new List<ReplayTrailPoint>();
            for (var index = logicalIndex; index >= Math.Max(0, logicalIndex - maximumPoints + 1); index--)
            {
                var candidate = replay.Seek(index).ReportedContacts.LastOrDefault(item => item.Id == contact.Id && IsTrackable(item));
                if (candidate is null) break;
                points.Add(new ReplayTrailPoint(candidate.X, candidate.Y, candidate.Status, index));
                if (candidate.Status == TouchStatus.Enter) break;
            }
            points.Reverse();
            if (points.Count > 1)
                trails.Add(new ReplayContactTrail(contact.Id, contact.Type, points));
        }
        return trails;
    }

    private static bool IsTrackable(ReplayContact contact) =>
        !contact.Invalid &&
        (contact.Status is TouchStatus.Enter or TouchStatus.Move or TouchStatus.Break ||
         contact.Type == TouchType.Palm && contact.Status == TouchStatus.NoFinger);
}
