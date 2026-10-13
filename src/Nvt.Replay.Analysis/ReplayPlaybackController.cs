namespace Nvt.Replay.Analysis;

/// <summary>Selects the timeline clock used to calculate playback delays.</summary>
public enum ReplayPlaybackClockDomain
{
    /// <summary>Uses recorded time, or display time when idle compression is enabled, to calculate playback delays.</summary>
    Recorded,
    /// <summary>Uses nominal frame time to calculate playback delays.</summary>
    Frame,
}

/// <summary>Represents a playback speed multiplier and whether playback uses maximum-speed frame batches.</summary>
public readonly record struct ReplayPlaybackRate
{
    private ReplayPlaybackRate(double multiplier, bool isMaximum)
    {
        Multiplier = multiplier;
        IsMaximum = isMaximum;
    }

    /// <summary>Gets the multiplier used as a divisor when scaling playback delays.</summary>
    public double Multiplier { get; }
    /// <summary>Indicates whether playback uses maximum-speed frame batches.</summary>
    public bool IsMaximum { get; }

    /// <summary>Gets a playback rate with a multiplier of one and maximum-speed mode disabled.</summary>
    public static ReplayPlaybackRate Normal { get; } = FromMultiplier(1);
    /// <summary>Gets a playback rate that enables maximum-speed frame batches.</summary>
    public static ReplayPlaybackRate Maximum { get; } = new(1, true);

    /// <summary>Creates a playback rate from a finite, positive speed multiplier.</summary>
    public static ReplayPlaybackRate FromMultiplier(double multiplier)
    {
        if (!double.IsFinite(multiplier) || multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));
        return new ReplayPlaybackRate(multiplier, false);
    }
}

/// <summary>Specifies whether playback should pause at Break or All Break events.</summary>
/// <param name="PauseOnBreak">Whether automatic pause points for valid contacts with Break status are enabled.</param>
/// <param name="PauseOnAllBreak">Whether automatic pause points for All Break frames are enabled.</param>
public sealed record ReplayPlaybackPauseOptions(bool PauseOnBreak, bool PauseOnAllBreak)
{
    /// <summary>Gets options that disable automatic pauses for both Break and All Break events.</summary>
    public static ReplayPlaybackPauseOptions None { get; } = new(false, false);
}

/// <summary>Identifies the review or contact event that caused playback to pause.</summary>
public enum ReplayPlaybackPauseReason
{
    /// <summary>Indicates a pause requested by the review pause-index provider.</summary>
    Review,
    /// <summary>Indicates a pause at a frame reporting a valid contact with Break status.</summary>
    Break,
    /// <summary>Indicates a pause at a frame marked as All Break.</summary>
    AllBreak,
}

/// <summary>Identifies the logical frame and reason for an automatic playback pause.</summary>
/// <param name="LogicalIndex">The logical frame index at which playback should pause.</param>
/// <param name="Reason">The review or contact event that caused the pause decision.</param>
/// <param name="SelectReview">Whether the pause requests selection of a review finding.</param>
public sealed record ReplayPlaybackPauseDecision(
    int LogicalIndex,
    ReplayPlaybackPauseReason Reason,
    bool SelectReview);

/// <summary>Describes whether playback started or was prevented by an empty timeline or invalid loop.</summary>
public enum ReplayPlaybackStartKind
{
    /// <summary>Indicates that playback started successfully.</summary>
    Started,
    /// <summary>Indicates that playback could not start because no timeline entries are loaded.</summary>
    Empty,
    /// <summary>Indicates that playback could not start because the enabled loop lacks a valid start-before-end range.</summary>
    InvalidLoop,
}

/// <summary>Reports the outcome, generation, and logical frame index of a playback start attempt.</summary>
/// <param name="Kind">The outcome of the playback start attempt.</param>
/// <param name="Generation">The controller generation associated with the start result.</param>
/// <param name="LogicalIndex">The current logical frame index, or minus one for an empty timeline.</param>
public readonly record struct ReplayPlaybackStart(
    ReplayPlaybackStartKind Kind,
    int Generation,
    int LogicalIndex);

/// <summary>Describes the next transport action or terminal result produced by playback.</summary>
public enum ReplayPlaybackOperationKind
{
    /// <summary>Indicates that the request generation is no longer accepted or playback is stopped.</summary>
    Stale,
    /// <summary>Indicates that the next visual advance has a scheduled due time.</summary>
    Wait,
    /// <summary>Indicates that playback advanced without reaching a pause decision.</summary>
    Move,
    /// <summary>Indicates that playback moved back to the configured loop start.</summary>
    LoopRestart,
    /// <summary>Indicates that playback stopped at a review, Break, or All Break pause point.</summary>
    Pause,
    /// <summary>Indicates that playback reached its final frame without looping.</summary>
    Complete,
    /// <summary>Indicates that playback stopped because the enabled loop range is invalid.</summary>
    InvalidLoop,
}

/// <summary>Reports a playback action with its generation, logical frame index, scheduled time, and optional pause decision.</summary>
/// <param name="Kind">The transport action or terminal result.</param>
/// <param name="Generation">The generation supplied for the playback request.</param>
/// <param name="LogicalIndex">The logical frame index associated with the operation.</param>
/// <param name="DueTime">The scheduled elapsed time associated with the operation.</param>
/// <param name="PauseDecision">The pause location and reason, or null when the operation carries no pause decision.</param>
public readonly record struct ReplayPlaybackOperation(
    ReplayPlaybackOperationKind Kind,
    int Generation,
    int LogicalIndex,
    TimeSpan DueTime,
    ReplayPlaybackPauseDecision? PauseDecision = null);

/// <summary>Reports whether playback settings changed and whether active playback needs a restarted schedule.</summary>
/// <param name="Changed">Whether the requested settings differed from the current settings.</param>
/// <param name="RestartRequired">Whether a settings change invalidated the active playback schedule.</param>
/// <param name="Generation">The controller generation after applying the settings.</param>
public readonly record struct ReplayPlaybackConfigurationChange(
    bool Changed,
    bool RestartRequired,
    int Generation);

/// <summary>
/// Pure replay transport state. The UI supplies an elapsed clock and renders the returned operations.
/// </summary>
public sealed class ReplayPlaybackController
{
    /// <summary>Defines the minimum interval between scheduled visual playback advances.</summary>
    public static readonly TimeSpan MinimumVisualInterval = TimeSpan.FromMilliseconds(16);
    private const int MaximumSpeedFrameAdvance = 50;

    private IReadOnlyList<ReplayTimelineEntry> timeline = Array.Empty<ReplayTimelineEntry>();
    private ReplayAutoPauseIndex? autoPauseIndex;
    private Func<int, int, int?>? firstReviewPauseIndex;
    private TimeSpan scheduledElapsed;

    /// <summary>Gets the number of entries in the loaded replay timeline.</summary>
    public int Count => timeline.Count;
    /// <summary>Gets the current logical frame index, or minus one when no frame is selected.</summary>
    public int CurrentIndex { get; private set; } = -1;
    /// <summary>Indicates whether the controller is accepting advances for active playback.</summary>
    public bool IsPlaying { get; private set; }
    /// <summary>Gets the version used to reject requests from an earlier playback schedule.</summary>
    public int Generation { get; private set; }
    /// <summary>Gets the clock domain used to calculate delays between timeline entries.</summary>
    public ReplayPlaybackClockDomain ClockDomain { get; private set; }
    /// <summary>Indicates whether recorded-clock playback uses the timeline's compressed display times.</summary>
    public bool CompressIdle { get; private set; } = true;
    /// <summary>Gets the speed multiplier and maximum-speed mode used for playback.</summary>
    public ReplayPlaybackRate Rate { get; private set; } = ReplayPlaybackRate.Normal;
    /// <summary>Indicates whether playback uses the configured loop range.</summary>
    public bool LoopEnabled { get; private set; }
    /// <summary>Gets the logical frame index to return to when looping, or null when no start is configured.</summary>
    public int? LoopStart { get; private set; }
    /// <summary>Gets the final logical frame index of the loop, or null when no end is configured.</summary>
    public int? LoopEnd { get; private set; }
    /// <summary>Gets the options that enable automatic pauses at Break and All Break frames.</summary>
    public ReplayPlaybackPauseOptions PauseOptions { get; private set; } = ReplayPlaybackPauseOptions.None;

    /// <summary>Loads a timeline and pause providers, resets playback and loop state, and increments the generation.</summary>
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

    /// <summary>Loads an empty timeline and resets playback, pause providers, and loop state.</summary>
    public void Clear() => Load(Array.Empty<ReplayTimelineEntry>());

    /// <summary>Selects the clamped logical frame index and returns it, or returns minus one for an empty timeline.</summary>
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

    /// <summary>Pauses playback, shifts the selected frame by the requested delta, and returns the clamped index.</summary>
    public int Step(int delta)
    {
        Pause();
        return Seek(CurrentIndex + delta);
    }

    /// <summary>Starts playback with a new generation unless the timeline is empty or the enabled loop is invalid.</summary>
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

    /// <summary>Stops active playback, increments its generation, and indicates whether playback was stopped.</summary>
    public bool Pause()
    {
        if (!IsPlaying) return false;
        IsPlaying = false;
        scheduledElapsed = TimeSpan.Zero;
        Generation++;
        return true;
    }

    /// <summary>Updates the clock, idle compression, and rate, invalidating the active schedule when settings change.</summary>
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

    /// <summary>Updates and clamps loop bounds, invalidating the active schedule when loop settings change.</summary>
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

    /// <summary>Replaces the options that enable automatic pauses at Break and All Break frames.</summary>
    public void ConfigureAutoPause(ReplayPlaybackPauseOptions options) =>
        PauseOptions = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Plans a wait, loop restart, or terminal result for the requested playback generation.</summary>
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

    /// <summary>Advances playback using elapsed time, stopping at the earliest enabled pause point or playback boundary.</summary>
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

    /// <summary>Calculates the nonnegative delay between two timeline entries using the selected clock and speed multiplier.</summary>
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
