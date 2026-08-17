namespace Nvt.Replay.Analysis;

public readonly record struct ReplayPlaybackPosition(int LogicalIndex, TimeSpan ScheduledElapsed);

public static class ReplayPlaybackSchedule
{
    public static ReplayPlaybackPosition CatchUp(
        int currentIndex,
        int endIndex,
        TimeSpan currentScheduledElapsed,
        TimeSpan actualElapsed,
        Func<int, int, TimeSpan> delayBetween,
        int? stopAtIndex = null)
    {
        ArgumentNullException.ThrowIfNull(delayBetween);
        if (currentIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(currentIndex));
        if (endIndex < currentIndex)
            throw new ArgumentOutOfRangeException(nameof(endIndex));
        if (stopAtIndex is { } stop && (stop <= currentIndex || stop > endIndex))
            throw new ArgumentOutOfRangeException(nameof(stopAtIndex));

        var index = currentIndex;
        var scheduledElapsed = currentScheduledElapsed;
        while (index < endIndex)
        {
            var next = index + 1;
            var delay = delayBetween(index, next);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
            var nextScheduledElapsed = scheduledElapsed + delay;
            if (nextScheduledElapsed > actualElapsed)
                break;

            index = next;
            scheduledElapsed = nextScheduledElapsed;
            if (index == stopAtIndex)
                break;
        }

        return new ReplayPlaybackPosition(index, scheduledElapsed);
    }
}
