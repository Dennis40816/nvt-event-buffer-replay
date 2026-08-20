using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

public enum ReplayAutomaticPauseKind
{
    Break,
    AllBreak,
}

public sealed record ReplayAutomaticPause(int LogicalIndex, ReplayAutomaticPauseKind Kind);

public sealed class ReplayAutoPauseIndex
{
    private readonly int[] breakIndices;
    private readonly int[] allBreakIndices;

    private ReplayAutoPauseIndex(int[] breakIndices, int[] allBreakIndices)
    {
        this.breakIndices = breakIndices;
        this.allBreakIndices = allBreakIndices;
    }

    public static ReplayAutoPauseIndex Create(ITouchReplaySession replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return Create(Enumerable.Range(0, replay.Count).Select(replay.Seek).ToArray());
    }

    public static ReplayAutoPauseIndex Create(IReadOnlyList<ITouchReplaySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var breaks = new List<int>();
        var allBreaks = new List<int>();
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            if (snapshot.AllBreak) allBreaks.Add(index);
            if (snapshot.ReportedContacts.Any(contact => !contact.Invalid && contact.Status == TouchStatus.Break))
                breaks.Add(index);
        }
        return new ReplayAutoPauseIndex(breaks.ToArray(), allBreaks.ToArray());
    }

    internal static ReplayAutoPauseIndex FromOwnedSortedIndices(int[] breakIndices, int[] allBreakIndices) =>
        new(breakIndices, allBreakIndices);

    public ReplayAutomaticPause? FirstAfter(
        int exclusiveStart,
        int inclusiveEnd,
        bool pauseOnBreak,
        bool pauseOnAllBreak)
    {
        if (inclusiveEnd <= exclusiveStart || (!pauseOnBreak && !pauseOnAllBreak)) return null;
        var breakIndex = pauseOnBreak ? FirstInRange(breakIndices, exclusiveStart, inclusiveEnd) : null;
        var allBreakIndex = pauseOnAllBreak ? FirstInRange(allBreakIndices, exclusiveStart, inclusiveEnd) : null;
        if (breakIndex is null && allBreakIndex is null) return null;
        if (allBreakIndex is null || breakIndex is not null && breakIndex <= allBreakIndex)
            return new ReplayAutomaticPause(breakIndex!.Value, ReplayAutomaticPauseKind.Break);
        return new ReplayAutomaticPause(allBreakIndex.Value, ReplayAutomaticPauseKind.AllBreak);
    }

    private static int? FirstInRange(int[] indices, int exclusiveStart, int inclusiveEnd)
    {
        var position = Array.BinarySearch(indices, exclusiveStart + 1);
        if (position < 0) position = ~position;
        return position < indices.Length && indices[position] <= inclusiveEnd ? indices[position] : null;
    }
}
