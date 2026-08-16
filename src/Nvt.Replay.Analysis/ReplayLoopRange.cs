namespace Nvt.Replay.Analysis;

public sealed record ReplayLoopRange(int StartLogicalIndex, int EndLogicalIndex);

public static class ReplayLoopRangePlanner
{
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
