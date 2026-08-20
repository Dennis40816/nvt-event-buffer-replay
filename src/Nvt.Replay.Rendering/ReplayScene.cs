using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public enum ReplayRenderMode
{
    HostState,
    ReportedFrame,
    Compare,
}

public enum ReplayTrailMode
{
    Recent,
    UntilBreak,
    Persistent,
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
    IReadOnlyList<string> MarkerLabels,
    bool ReverseX,
    bool ReverseY,
    bool SwapAxes)
{
    public ReplayExtent ViewExtent { get; } = SwapAxes
        ? new ReplayExtent(Extent.MaximumY, Extent.MaximumX)
        : Extent;

    public (double X, double Y) ViewCoordinates(double x, double y) =>
        SwapAxes ? (y, x) : (x, y);
}

public sealed record ReplayTrailPoint(ushort X, ushort Y, TouchStatus Status, int LogicalIndex);

public sealed record ReplayContactTrail(
    byte Id,
    TouchType Type,
    IReadOnlyList<ReplayTrailPoint> Points,
    bool IsComplete = false);

public static class ReplaySceneFactory
{
    public static ReplayScene Create(
        ITouchReplaySnapshot snapshot,
        int totalFrames,
        ReplayExtent extent,
        IEnumerable<ReplayDiagnostic>? diagnostics = null,
        IEnumerable<ReplayMarker>? markers = null,
        IReadOnlyList<ReplayContactTrail>? contactTrails = null,
        bool reverseX = false,
        bool reverseY = false,
        bool swapAxes = false)
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
                .Select(item => item.Label).ToArray() ?? [],
            reverseX,
            reverseY,
            swapAxes);
    }

    public static IReadOnlyList<ReplayContactTrail> BuildTrails(
        ITouchReplaySession replay,
        int logicalIndex,
        int maximumPoints = 8)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (logicalIndex < 0 || logicalIndex >= replay.Count)
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        if (maximumPoints is < 2 or > 10)
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

public sealed class ReplayTrailHistory
{
    private readonly IReadOnlyDictionary<byte, IReadOnlyList<Gesture>> gesturesById;
    private readonly IReadOnlyList<IReadOnlySet<byte>> reportedIdsByFrame;
    private readonly IReadOnlyList<IReadOnlySet<byte>> activeIdsByFrame;

    private ReplayTrailHistory(
        IReadOnlyDictionary<byte, IReadOnlyList<Gesture>> gesturesById,
        IReadOnlyList<IReadOnlySet<byte>> reportedIdsByFrame,
        IReadOnlyList<IReadOnlySet<byte>> activeIdsByFrame)
    {
        this.gesturesById = gesturesById;
        this.reportedIdsByFrame = reportedIdsByFrame;
        this.activeIdsByFrame = activeIdsByFrame;
    }

    public int Count => reportedIdsByFrame.Count;

    public static ReplayTrailHistory Create(
        ITouchReplaySession replay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return Create(replay.EnumerateSnapshots(cancellationToken).ToArray(), cancellationToken);
    }

    public static ReplayTrailHistory Create(
        IReadOnlyList<ITouchReplaySnapshot> snapshots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var completed = new Dictionary<byte, List<Gesture>>();
        var open = new Dictionary<byte, GestureBuilder>();
        var reportedIds = new IReadOnlySet<byte>[snapshots.Count];
        var activeIds = new IReadOnlySet<byte>[snapshots.Count];

        for (var logicalIndex = 0; logicalIndex < snapshots.Count; logicalIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = snapshots[logicalIndex];
            var reported = snapshot.ReportedContacts.Where(IsTrackable).OrderBy(contact => contact.Id).ToArray();
            reportedIds[logicalIndex] = reported.Select(contact => contact.Id).ToHashSet();
            activeIds[logicalIndex] = snapshot.HostContacts
                .Where(contact => contact.IsActive && !contact.Invalid)
                .Select(contact => contact.Id)
                .ToHashSet();

            if (!snapshot.HostStateUpdated)
            {
                foreach (var builder in open.Values.ToArray()) Finish(builder, logicalIndex - 1, completed);
                open.Clear();
                continue;
            }

            foreach (var contact in reported)
            {
                if (contact.Status == TouchStatus.Enter && open.Remove(contact.Id, out var prior))
                    Finish(prior, logicalIndex - 1, completed);

                if (!open.TryGetValue(contact.Id, out var builder))
                {
                    builder = new GestureBuilder(contact.Id, contact.Type, logicalIndex);
                    open[contact.Id] = builder;
                }
                builder.Points.Add(new ReplayTrailPoint(contact.X, contact.Y, contact.Status, logicalIndex));

                if (contact.Status == TouchStatus.Break)
                {
                    Finish(builder, logicalIndex, completed);
                    open.Remove(contact.Id);
                }
            }

            if (activeIds[logicalIndex].Count == 0)
            {
                foreach (var builder in open.Values.ToArray()) Finish(builder, logicalIndex - 1, completed);
                open.Clear();
            }
        }

        foreach (var builder in open.Values)
            Finish(builder, Math.Max(builder.StartLogicalIndex, snapshots.Count - 1), completed);

        return new ReplayTrailHistory(
            completed.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<Gesture>)pair.Value.OrderBy(gesture => gesture.StartLogicalIndex).ToArray()),
            reportedIds,
            activeIds);
    }

    public IReadOnlyList<ReplayContactTrail> Build(
        int logicalIndex,
        ReplayTrailMode mode,
        int recentPointCount = 10,
        int minimumLogicalIndex = 0)
    {
        if (logicalIndex < 0 || logicalIndex >= Count)
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        if (recentPointCount is < 2 or > 120)
            throw new ArgumentOutOfRangeException(nameof(recentPointCount));
        minimumLogicalIndex = Math.Clamp(minimumLogicalIndex, 0, Count);

        var selectedIds = mode switch
        {
            ReplayTrailMode.Recent => reportedIdsByFrame[logicalIndex],
            ReplayTrailMode.UntilBreak => activeIdsByFrame[logicalIndex],
            _ => gesturesById.Keys.ToHashSet(),
        };
        var result = new List<ReplayContactTrail>();
        foreach (var id in selectedIds.Order())
        {
            if (!gesturesById.TryGetValue(id, out var gestures)) continue;
            var candidates = mode == ReplayTrailMode.Persistent
                ? gestures.Where(gesture => gesture.StartLogicalIndex <= logicalIndex)
                : gestures.Where(gesture =>
                    gesture.StartLogicalIndex <= logicalIndex && gesture.EndLogicalIndex >= logicalIndex).TakeLast(1);

            foreach (var gesture in candidates)
            {
                IReadOnlyList<ReplayTrailPoint> points = Slice(
                    gesture.Points,
                    minimumLogicalIndex,
                    logicalIndex);
                if (mode == ReplayTrailMode.Recent && points.Count > recentPointCount)
                    points = SliceRange(points, points.Count - recentPointCount, recentPointCount);
                if (points.Count > 1)
                    result.Add(new ReplayContactTrail(
                        gesture.Id,
                        gesture.Type,
                        points,
                        IsComplete: gesture.EndLogicalIndex <= logicalIndex));
            }
        }
        return result;
    }

    private static IReadOnlyList<ReplayTrailPoint> Slice(
        IReadOnlyList<ReplayTrailPoint> points,
        int minimumLogicalIndex,
        int maximumLogicalIndex)
    {
        var start = LowerBound(points, minimumLogicalIndex);
        var end = UpperBound(points, maximumLogicalIndex);
        return SliceRange(points, start, Math.Max(0, end - start));
    }

    private static IReadOnlyList<ReplayTrailPoint> SliceRange(
        IReadOnlyList<ReplayTrailPoint> points,
        int start,
        int count)
    {
        if (count == 0) return [];
        if (start == 0 && count == points.Count) return points;
        if (points is ReplayTrailPoint[] array) return new ArraySegment<ReplayTrailPoint>(array, start, count);
        var result = new ReplayTrailPoint[count];
        for (var index = 0; index < count; index++) result[index] = points[start + index];
        return result;
    }

    private static int LowerBound(IReadOnlyList<ReplayTrailPoint> points, int logicalIndex)
    {
        var low = 0;
        var high = points.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (points[middle].LogicalIndex < logicalIndex) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int UpperBound(IReadOnlyList<ReplayTrailPoint> points, int logicalIndex)
    {
        var low = 0;
        var high = points.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (points[middle].LogicalIndex <= logicalIndex) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static void Finish(
        GestureBuilder builder,
        int endLogicalIndex,
        IDictionary<byte, List<Gesture>> completed)
    {
        if (builder.Points.Count < 2) return;
        if (!completed.TryGetValue(builder.Id, out var gestures))
        {
            gestures = [];
            completed[builder.Id] = gestures;
        }
        gestures.Add(new Gesture(
            builder.Id,
            builder.Type,
            builder.StartLogicalIndex,
            Math.Max(builder.StartLogicalIndex, endLogicalIndex),
            builder.Points.ToArray()));
    }

    private static bool IsTrackable(ReplayContact contact) =>
        !contact.Invalid &&
        (contact.Status is TouchStatus.Enter or TouchStatus.Move or TouchStatus.Break ||
         contact.Type == TouchType.Palm && contact.Status == TouchStatus.NoFinger);

    private sealed class GestureBuilder(byte id, TouchType type, int startLogicalIndex)
    {
        public byte Id { get; } = id;
        public TouchType Type { get; } = type;
        public int StartLogicalIndex { get; } = startLogicalIndex;
        public List<ReplayTrailPoint> Points { get; } = [];
    }

    private sealed record Gesture(
        byte Id,
        TouchType Type,
        int StartLogicalIndex,
        int EndLogicalIndex,
        IReadOnlyList<ReplayTrailPoint> Points);
}
