namespace Nvt.Replay.Analysis;

/// <summary>Holds the start and end logical frame indices for a replay loop.</summary>
/// <param name="StartLogicalIndex">The logical index of the frame where the replay loop starts.</param>
/// <param name="EndLogicalIndex">The logical index of the frame where the replay loop ends.</param>
public sealed record ReplayLoopRange(int StartLogicalIndex, int EndLogicalIndex);

/// <summary>Plans default replay loop ranges using logical frame indices and the frame interval.</summary>
public static class ReplayLoopRangePlanner
{
    /// <summary>Creates a replay loop near the current logical frame, targeting a one-second span within the available frames.</summary>
    public static ReplayLoopRange CreateDefault(
        int currentLogicalIndex,
        int frameCount,
        TimeSpan frameInterval)
    {
        if (frameCount < 2)
            throw new ArgumentOutOfRangeException(nameof(frameCount), "A loop requires at least two frames.");

        var last = frameCount - 1;
        var intervalTicks = Math.Max(1, frameInterval.Ticks);
        var oneSecondSpan = Math.Max(1, (int)(TimeSpan.TicksPerSecond / intervalTicks));
        var desiredSpan = Math.Min(last, oneSecondSpan);
        var current = Math.Clamp(currentLogicalIndex, 0, last);
        var start = Math.Min(current, last - 1);
        var end = Math.Min(last, start + desiredSpan);

        if (end - start < desiredSpan)
            start = Math.Max(0, end - desiredSpan);

        return new ReplayLoopRange(start, end);
    }
}
