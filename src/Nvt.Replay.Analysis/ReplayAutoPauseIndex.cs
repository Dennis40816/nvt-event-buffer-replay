using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

/// <summary>Identifies the reported condition that can trigger an automatic replay pause.</summary>
public enum ReplayAutomaticPauseKind
{
    /// <summary>Indicates a reported contact with Break status that is not marked invalid.</summary>
    Break,
    /// <summary>Indicates a snapshot with its AllBreak flag set.</summary>
    AllBreak,
}

/// <summary>Stores the logical frame index and trigger kind of an automatic replay pause.</summary>
/// <param name="LogicalIndex">The zero-based logical frame index at which replay pauses.</param>
/// <param name="Kind">The reported condition that triggers the pause.</param>
public sealed record ReplayAutomaticPause(int LogicalIndex, ReplayAutomaticPauseKind Kind);

/// <summary>Indexes snapshots containing non-invalid Break contacts or an AllBreak flag for automatic replay pauses.</summary>
public sealed class ReplayAutoPauseIndex
{
    private readonly int[] breakIndices;
    private readonly int[] allBreakIndices;

    private ReplayAutoPauseIndex(int[] breakIndices, int[] allBreakIndices)
    {
        this.breakIndices = breakIndices;
        this.allBreakIndices = allBreakIndices;
    }

    /// <summary>Builds an automatic pause index by seeking every logical frame in the replay session.</summary>
    public static ReplayAutoPauseIndex Create(ITouchReplaySession replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return Create(Enumerable.Range(0, replay.Count).Select(replay.Seek).ToArray());
    }

    /// <summary>Builds an automatic pause index from snapshot positions with non-invalid Break contacts or an AllBreak flag.</summary>
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

    /// <summary>Finds the first enabled pause after the start through the end, preferring Break when indices match.</summary>
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
