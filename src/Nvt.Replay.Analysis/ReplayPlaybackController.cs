namespace Nvt.Replay.Analysis;

public enum ReplayPlaybackClockDomain
{
    Recorded,
    Frame,
}

public readonly record struct ReplayPlaybackRate
{
    private ReplayPlaybackRate(double multiplier, bool isMaximum)
    {
        Multiplier = multiplier;
        IsMaximum = isMaximum;
    }

    public double Multiplier { get; }
    public bool IsMaximum { get; }

    public static ReplayPlaybackRate Normal { get; } = FromMultiplier(1);
    public static ReplayPlaybackRate Maximum { get; } = new(1, true);

    public static ReplayPlaybackRate FromMultiplier(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        return new ReplayPlaybackRate(multiplier, false);
    }
}

public sealed record ReplayPlaybackPauseOptions(bool PauseOnBreak, bool PauseOnAllBreak)
{
    public static ReplayPlaybackPauseOptions None { get; } = new(false, false);
}

public enum ReplayPlaybackPauseReason
{
    Review,
    Break,
    AllBreak,
}

public sealed record ReplayPlaybackPauseDecision(
    int LogicalIndex,
    ReplayPlaybackPauseReason Reason,
    bool SelectReview);

public enum ReplayPlaybackStartKind
{
    Started,
    Empty,
    InvalidLoop,
}

public readonly record struct ReplayPlaybackStart(
    ReplayPlaybackStartKind Kind,
    int Generation,
    int LogicalIndex);

public enum ReplayPlaybackOperationKind
{
    Stale,
    Wait,
    Move,
    LoopRestart,
    Pause,
    Complete,
    InvalidLoop,
}

public readonly record struct ReplayPlaybackOperation(
    ReplayPlaybackOperationKind Kind,
    int Generation,
    int LogicalIndex,
    TimeSpan DueTime,
    ReplayPlaybackPauseDecision? PauseDecision = null);

public readonly record struct ReplayPlaybackConfigurationChange(
    bool Changed,
    bool RestartRequired,
    int Generation);

/// <summary>
/// Pure replay transport state. The UI supplies an elapsed clock and renders the returned operations.
/// </summary>
public sealed class ReplayPlaybackController
{
    public static readonly TimeSpan MinimumVisualInterval = TimeSpan.FromMilliseconds(16);
    private const int MaximumSpeedFrameAdvance = 50;

    private IReadOnlyList<ReplayTimelineEntry> timeline = Array.Empty<ReplayTimelineEntry>();
    private ReplayAutoPauseIndex? autoPauseIndex;
    private Func<int, int, int?>? firstReviewPauseIndex;
    private TimeSpan scheduledElapsed;

    public int Count => timeline.Count;
    public int CurrentIndex { get; private set; } = -1;
    public bool IsPlaying { get; private set; }
    public int Generation { get; private set; }
    public ReplayPlaybackClockDomain ClockDomain { get; private set; }
    public bool CompressIdle { get; private set; } = true;
    public ReplayPlaybackRate Rate { get; private set; } = ReplayPlaybackRate.Normal;
    public bool LoopEnabled { get; private set; }
    public int? LoopStart { get; private set; }
    public int? LoopEnd { get; private set; }
    public ReplayPlaybackPauseOptions PauseOptions { get; private set; } = ReplayPlaybackPauseOptions.None;

    public void Load(
        IReadOnlyList<ReplayTimelineEntry> nextTimeline,
        ReplayAutoPauseIndex? nextAutoPauseIndex = null,
        Func<int, int, int?>? nextFirstReviewPauseIndex = null)
    {
        ArgumentNullException.ThrowIfNull(nextTimeline);
        timeline = nextTimeline;
        autoPauseIndex = nextAutoPauseIndex;
        firstReviewPauseIndex = nextFirstReviewPauseIndex;
        CurrentIndex = -1;
        LoopEnabled = false;
        LoopStart = null;
        LoopEnd = null;
        scheduledElapsed = TimeSpan.Zero;
        IsPlaying = false;
        Generation++;
    }

    public void Clear() => Load(Array.Empty<ReplayTimelineEntry>());

    public int Seek(int logicalIndex)
    {
        if (timeline.Count == 0)
        {
            CurrentIndex = -1;
            return CurrentIndex;
        }

        var clamped = Math.Clamp(logicalIndex, 0, timeline.Count - 1);
        if (clamped != CurrentIndex)
        {
            CurrentIndex = clamped;
            scheduledElapsed = TimeSpan.Zero;
        }
        return CurrentIndex;
    }

    public int Step(int delta)
    {
        Pause();
        return Seek(CurrentIndex + delta);
    }

    public ReplayPlaybackStart Play()
    {
        if (timeline.Count == 0)
            return new ReplayPlaybackStart(ReplayPlaybackStartKind.Empty, Generation, -1);
        if (HasInvalidLoop())
            return new ReplayPlaybackStart(ReplayPlaybackStartKind.InvalidLoop, Generation, CurrentIndex);

        if (CurrentIndex >= timeline.Count - 1)
            CurrentIndex = LoopEnabled ? LoopStart ?? 0 : 0;
        else if (CurrentIndex < 0)
            CurrentIndex = 0;

        scheduledElapsed = TimeSpan.Zero;
        IsPlaying = true;
        Generation++;
        return new ReplayPlaybackStart(ReplayPlaybackStartKind.Started, Generation, CurrentIndex);
    }

    public bool Pause()
    {
        if (!IsPlaying) return false;
        IsPlaying = false;
        scheduledElapsed = TimeSpan.Zero;
        Generation++;
        return true;
    }

    public ReplayPlaybackConfigurationChange ConfigureTiming(
        ReplayPlaybackClockDomain clockDomain,
        bool compressIdle,
        ReplayPlaybackRate rate)
    {
        if (clockDomain == ClockDomain && compressIdle == CompressIdle && rate == Rate)
            return new ReplayPlaybackConfigurationChange(false, false, Generation);

        ClockDomain = clockDomain;
        CompressIdle = compressIdle;
        Rate = rate;
        return InvalidateActiveOperation();
    }

    public ReplayPlaybackConfigurationChange ConfigureLoop(bool enabled, int? start, int? end)
    {
        start = ClampOptional(start);
        end = ClampOptional(end);
        if (enabled == LoopEnabled && start == LoopStart && end == LoopEnd)
            return new ReplayPlaybackConfigurationChange(false, false, Generation);

        LoopEnabled = enabled;
        LoopStart = start;
        LoopEnd = end;
        return InvalidateActiveOperation();
    }

    public void ConfigureAutoPause(ReplayPlaybackPauseOptions options) =>
        PauseOptions = options ?? throw new ArgumentNullException(nameof(options));

    public ReplayPlaybackOperation PlanNext(int generation)
    {
        if (!Accepts(generation)) return Stale(generation);
        var end = PlaybackEnd;
        if (CurrentIndex >= end)
        {
            if (!LoopEnabled)
            {
                IsPlaying = false;
                return Operation(ReplayPlaybackOperationKind.Complete, generation, CurrentIndex);
            }
            if (HasInvalidLoop())
            {
                IsPlaying = false;
                return Operation(ReplayPlaybackOperationKind.InvalidLoop, generation, CurrentIndex);
            }

            CurrentIndex = LoopStart!.Value;
            scheduledElapsed = TimeSpan.Zero;
            return Operation(ReplayPlaybackOperationKind.LoopRestart, generation, CurrentIndex);
        }

        var due = Rate.IsMaximum
            ? scheduledElapsed + MinimumVisualInterval
            : ReplayPlaybackSchedule.NextVisualDue(
                scheduledElapsed,
                DelayBetween(CurrentIndex, CurrentIndex + 1),
                MinimumVisualInterval);
        return new ReplayPlaybackOperation(
            ReplayPlaybackOperationKind.Wait,
            generation,
            CurrentIndex,
            due);
    }

    public ReplayPlaybackOperation Advance(int generation, TimeSpan actualElapsed)
    {
        if (!Accepts(generation)) return Stale(generation);
        if (actualElapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(actualElapsed));

        var end = PlaybackEnd;
        if (CurrentIndex >= end) return PlanNext(generation);

        var pause = FirstPause(CurrentIndex, end);
        int next;
        if (Rate.IsMaximum)
        {
            var elapsedTicks = Math.Max(
                MinimumVisualInterval.Ticks,
                actualElapsed.Ticks - scheduledElapsed.Ticks);
            var dueSteps = Math.Max(1L, elapsedTicks / MinimumVisualInterval.Ticks);
            var availableSteps = Math.Max(1L, (end - CurrentIndex + MaximumSpeedFrameAdvance - 1L) / MaximumSpeedFrameAdvance);
            var steps = Math.Min(dueSteps, availableSteps);
            var advance = Math.Min((long)end - CurrentIndex, steps * MaximumSpeedFrameAdvance);
            next = CurrentIndex + (int)advance;
            if (pause is { } decision && decision.LogicalIndex <= next) next = decision.LogicalIndex;
            scheduledElapsed = AddSaturated(scheduledElapsed, MultiplySaturated(MinimumVisualInterval, steps));
        }
        else
        {
            var position = ReplayPlaybackSchedule.CatchUp(
                CurrentIndex,
                end,
                scheduledElapsed,
                actualElapsed,
                DelayBetween,
                pause?.LogicalIndex);
            next = position.LogicalIndex;
            scheduledElapsed = position.ScheduledElapsed;
        }

        CurrentIndex = next;
        if (pause?.LogicalIndex == next)
        {
            IsPlaying = false;
            return new ReplayPlaybackOperation(
                ReplayPlaybackOperationKind.Pause,
                generation,
                next,
                scheduledElapsed,
                pause);
        }
        return Operation(ReplayPlaybackOperationKind.Move, generation, next);
    }

    public TimeSpan DelayBetween(int current, int next)
    {
        if ((uint)current >= (uint)timeline.Count) throw new ArgumentOutOfRangeException(nameof(current));
        if ((uint)next >= (uint)timeline.Count || next < current) throw new ArgumentOutOfRangeException(nameof(next));
        var currentEntry = timeline[current];
        var nextEntry = timeline[next];
        var delay = ClockDomain switch
        {
            ReplayPlaybackClockDomain.Frame => nextEntry.FrameTime - currentEntry.FrameTime,
            _ when CompressIdle => nextEntry.DisplayTime - currentEntry.DisplayTime,
            _ => nextEntry.RecordedTime - currentEntry.RecordedTime,
        };
        if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
        return ScaleSaturated(delay, Rate.Multiplier);
    }

    private int PlaybackEnd => LoopEnabled ? LoopEnd ?? timeline.Count - 1 : timeline.Count - 1;

    private bool HasInvalidLoop() =>
        LoopEnabled &&
        (LoopStart is null || LoopEnd is null || LoopStart.Value >= LoopEnd.Value);

    private ReplayPlaybackPauseDecision? FirstPause(int exclusiveStart, int inclusiveEnd)
    {
        var review = firstReviewPauseIndex?.Invoke(exclusiveStart, inclusiveEnd);
        var automatic = autoPauseIndex?.FirstAfter(
            exclusiveStart,
            inclusiveEnd,
            PauseOptions.PauseOnBreak,
            PauseOptions.PauseOnAllBreak);
        if (review is { } reviewIndex && (automatic is null || reviewIndex <= automatic.LogicalIndex))
            return new ReplayPlaybackPauseDecision(reviewIndex, ReplayPlaybackPauseReason.Review, true);
        return automatic?.Kind switch
        {
            ReplayAutomaticPauseKind.AllBreak => new ReplayPlaybackPauseDecision(
                automatic.LogicalIndex,
                ReplayPlaybackPauseReason.AllBreak,
                false),
            ReplayAutomaticPauseKind.Break => new ReplayPlaybackPauseDecision(
                automatic.LogicalIndex,
                ReplayPlaybackPauseReason.Break,
                false),
            _ => null,
        };
    }

    private ReplayPlaybackConfigurationChange InvalidateActiveOperation()
    {
        if (!IsPlaying)
            return new ReplayPlaybackConfigurationChange(true, false, Generation);
        scheduledElapsed = TimeSpan.Zero;
        Generation++;
        return new ReplayPlaybackConfigurationChange(true, true, Generation);
    }

    private int? ClampOptional(int? value) => value is null || timeline.Count == 0
        ? value
        : Math.Clamp(value.Value, 0, timeline.Count - 1);

    private bool Accepts(int generation) => IsPlaying && generation == Generation;

    private ReplayPlaybackOperation Operation(
        ReplayPlaybackOperationKind kind,
        int generation,
        int logicalIndex) =>
        new(kind, generation, logicalIndex, scheduledElapsed);

    private ReplayPlaybackOperation Stale(int generation) =>
        new(ReplayPlaybackOperationKind.Stale, generation, CurrentIndex, scheduledElapsed);

    private static TimeSpan ScaleSaturated(TimeSpan value, double divisor)
    {
        var ticks = value.Ticks / divisor;
        return ticks >= TimeSpan.MaxValue.Ticks
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(Math.Max(0, (long)ticks));
    }

    private static TimeSpan MultiplySaturated(TimeSpan value, long multiplier)
    {
        if (multiplier <= 0 || value <= TimeSpan.Zero) return TimeSpan.Zero;
        return value.Ticks > TimeSpan.MaxValue.Ticks / multiplier
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(value.Ticks * multiplier);
    }

    private static TimeSpan AddSaturated(TimeSpan left, TimeSpan right) =>
        left.Ticks > TimeSpan.MaxValue.Ticks - right.Ticks
            ? TimeSpan.MaxValue
            : left + right;
}
