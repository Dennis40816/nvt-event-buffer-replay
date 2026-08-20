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
        ArgumentNullException.ThrowIfNull(contacts);
        double maximumX = 0;
        double maximumY = 0;
        var any = false;
        foreach (var contact in contacts)
        {
            any = true;
            maximumX = Math.Max(maximumX, contact.X);
            maximumY = Math.Max(maximumY, contact.Y);
        }
        return new ReplayExtent(
            Nice(any ? maximumX : 1920, 1920),
            Nice(any ? maximumY : 1080, 1080));
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

public readonly record struct ReplayTrailPoint(ushort X, ushort Y, TouchStatus Status, int LogicalIndex);

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

    internal static ReplayScene CreateProjected(
        ITouchReplaySnapshot snapshot,
        int totalFrames,
        ReplayExtent extent,
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyList<string> markerLabels,
        IReadOnlyList<ReplayContactTrail>? contactTrails = null,
        bool reverseX = false,
        bool reverseY = false,
        bool swapAxes = false) =>
        new(
            snapshot.LogicalIndex,
            totalFrames,
            snapshot.Timeline,
            snapshot.ReportedContacts,
            snapshot.HostContacts,
            snapshot.GlobalPalm,
            extent,
            contactTrails ?? [],
            diagnostics,
            markerLabels,
            reverseX,
            reverseY,
            swapAxes);

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
    private readonly uint[] reportedIdMasksByFrame;
    private readonly uint[] activeIdMasksByFrame;
    private readonly uint persistentIdMask;

    private ReplayTrailHistory(
        IReadOnlyDictionary<byte, IReadOnlyList<Gesture>> gesturesById,
        uint[] reportedIdMasksByFrame,
        uint[] activeIdMasksByFrame)
    {
        this.gesturesById = gesturesById;
        this.reportedIdMasksByFrame = reportedIdMasksByFrame;
        this.activeIdMasksByFrame = activeIdMasksByFrame;
        persistentIdMask = Mask(gesturesById.Keys);
        Statistics = new ReplayTrailStorageStatistics(
            gesturesById.Values.Sum(gestures => gestures.Count),
            gesturesById.Values.SelectMany(gestures => gestures).Sum(gesture => gesture.Points.Count),
            gesturesById.Values.SelectMany(gestures => gestures)
                .Sum(gesture => gesture.Points.Count / ReplayTrailChunker.DefaultChunkSize));
    }

    public int Count => reportedIdMasksByFrame.Length;

    public ReplayTrailStorageStatistics Statistics { get; }

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
        var reportedIdMasks = new uint[snapshots.Count];
        var activeIdMasks = new uint[snapshots.Count];

        for (var logicalIndex = 0; logicalIndex < snapshots.Count; logicalIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = snapshots[logicalIndex];
            var reportedIdMask = 0u;
            for (var contactIndex = 0; contactIndex < snapshot.ReportedContacts.Count; contactIndex++)
            {
                var contact = snapshot.ReportedContacts[contactIndex];
                if (IsTrackable(contact)) reportedIdMask |= Bit(contact.Id);
            }
            reportedIdMasks[logicalIndex] = reportedIdMask;

            var activeIdMask = 0u;
            for (var contactIndex = 0; contactIndex < snapshot.HostContacts.Count; contactIndex++)
            {
                var contact = snapshot.HostContacts[contactIndex];
                if (contact.IsActive && !contact.Invalid) activeIdMask |= Bit(contact.Id);
            }
            activeIdMasks[logicalIndex] = activeIdMask;

            if (!snapshot.HostStateUpdated)
            {
                foreach (var builder in open.Values) Finish(builder, logicalIndex - 1, completed);
                open.Clear();
                continue;
            }

            for (var contactIndex = 0; contactIndex < snapshot.ReportedContacts.Count; contactIndex++)
            {
                var contact = snapshot.ReportedContacts[contactIndex];
                if (!IsTrackable(contact)) continue;
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

            if (activeIdMask == 0)
            {
                foreach (var builder in open.Values) Finish(builder, logicalIndex - 1, completed);
                open.Clear();
            }
        }

        foreach (var builder in open.Values)
            Finish(builder, Math.Max(builder.StartLogicalIndex, snapshots.Count - 1), completed);

        return new ReplayTrailHistory(
            completed.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<Gesture>)pair.Value.OrderBy(gesture => gesture.StartLogicalIndex).ToArray()),
            reportedIdMasks,
            activeIdMasks);
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

        var selectedIdMask = mode switch
        {
            ReplayTrailMode.Recent => reportedIdMasksByFrame[logicalIndex],
            ReplayTrailMode.UntilBreak => activeIdMasksByFrame[logicalIndex],
            _ => persistentIdMask,
        };
        var result = new List<ReplayContactTrail>();
        for (byte id = 0; id < 32; id++)
        {
            if ((selectedIdMask & Bit(id)) == 0) continue;
            if (!gesturesById.TryGetValue(id, out var gestures)) continue;
            if (mode == ReplayTrailMode.Persistent)
            {
                foreach (var gesture in gestures)
                {
                    if (gesture.StartLogicalIndex > logicalIndex) break;
                    AddTrail(result, gesture, logicalIndex, minimumLogicalIndex, mode, recentPointCount);
                }
                continue;
            }

            for (var gestureIndex = gestures.Count - 1; gestureIndex >= 0; gestureIndex--)
            {
                var gesture = gestures[gestureIndex];
                if (gesture.StartLogicalIndex > logicalIndex) continue;
                if (gesture.EndLogicalIndex >= logicalIndex)
                    AddTrail(result, gesture, logicalIndex, minimumLogicalIndex, mode, recentPointCount);
                break;
            }
        }
        return result;
    }

    private static void AddTrail(
        ICollection<ReplayContactTrail> result,
        Gesture gesture,
        int logicalIndex,
        int minimumLogicalIndex,
        ReplayTrailMode mode,
        int recentPointCount)
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

    private static uint Bit(byte id) => id < 32 ? 1u << id : 0;

    private static uint Mask(IEnumerable<byte> ids)
    {
        var result = 0u;
        foreach (var id in ids) result |= Bit(id);
        return result;
    }

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

public readonly record struct ReplayTrailStorageStatistics(
    int GestureCount,
    int PointCount,
    int ReusableFullChunkCount);
