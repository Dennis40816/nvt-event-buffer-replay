namespace Nvt.Replay.Analysis;

/// <summary>Holds a logical replay index and its scheduled elapsed time.</summary>
/// <param name="LogicalIndex">The logical replay index.</param>
/// <param name="ScheduledElapsed">The scheduled elapsed time at the logical replay index.</param>
public readonly record struct ReplayPlaybackPosition(int LogicalIndex, TimeSpan ScheduledElapsed);

/// <summary>Calculates visual update due times and advances replay positions through frames that are due.</summary>
public static class ReplayPlaybackSchedule
{
    /// <summary>The default maximum number of frame advances allowed in one catch-up operation.</summary>
    public const int DefaultMaximumAdvance = 512;

    /// <summary>Returns the next visual due time using the greater of the nonnegative frame delay and minimum visual interval.</summary>
    public static TimeSpan NextVisualDue(
        TimeSpan currentScheduledElapsed,
        TimeSpan nextFrameDelay,
        TimeSpan minimumVisualInterval)
    {
        if (nextFrameDelay < TimeSpan.Zero)
            nextFrameDelay = TimeSpan.Zero;
        if (minimumVisualInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumVisualInterval));

        var logicalDue = currentScheduledElapsed + nextFrameDelay;
        var visualDue = currentScheduledElapsed + minimumVisualInterval;
        return logicalDue > visualDue ? logicalDue : visualDue;
    }

    /// <summary>Advances through due replay frames until reaching the end, an optional stop index, or the advance limit.</summary>
    public static ReplayPlaybackPosition CatchUp(
        int currentIndex,
        int endIndex,
        TimeSpan currentScheduledElapsed,
        TimeSpan actualElapsed,
        Func<int, int, TimeSpan> delayBetween,
        int? stopAtIndex = null,
        int maximumAdvance = DefaultMaximumAdvance)
    {
        ArgumentNullException.ThrowIfNull(delayBetween);
        if (currentIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(currentIndex));
        if (endIndex < currentIndex)
            throw new ArgumentOutOfRangeException(nameof(endIndex));
        if (stopAtIndex is { } stop && (stop <= currentIndex || stop > endIndex))
            throw new ArgumentOutOfRangeException(nameof(stopAtIndex));
        if (maximumAdvance < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumAdvance));

        var index = currentIndex;
        var scheduledElapsed = currentScheduledElapsed;
        var advanced = 0;
        while (index < endIndex && advanced < maximumAdvance)
        {
            var next = index + 1;
            var delay = delayBetween(index, next);
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
            var nextScheduledElapsed = scheduledElapsed + delay;
            if (nextScheduledElapsed > actualElapsed)
                break;

            index = next;
            advanced++;
            scheduledElapsed = nextScheduledElapsed;
            if (index == stopAtIndex)
                break;
        }

        return new ReplayPlaybackPosition(index, scheduledElapsed);
    }
}
