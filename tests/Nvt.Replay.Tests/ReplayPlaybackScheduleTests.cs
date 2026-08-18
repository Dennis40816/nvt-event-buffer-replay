using Nvt.Replay.Analysis;

namespace Nvt.Replay.Tests;

public sealed class ReplayPlaybackScheduleTests
{
    [Theory]
    [InlineData(0.833, 8, 8)]
    [InlineData(8.333, 8, 8.333)]
    [InlineData(833.333, 8, 833.333)]
    public void Next_visual_due_caps_render_frequency_without_changing_slow_frame_timing(
        double frameDelayMilliseconds,
        double minimumVisualIntervalMilliseconds,
        double expectedMilliseconds)
    {
        var due = ReplayPlaybackSchedule.NextVisualDue(
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(frameDelayMilliseconds),
            TimeSpan.FromMilliseconds(minimumVisualIntervalMilliseconds));

        Assert.Equal(expectedMilliseconds, due.TotalMilliseconds, precision: 3);
    }

    [Fact]
    public void Catch_up_skips_late_visual_frames_without_shifting_the_absolute_schedule()
    {
        var frameInterval = TimeSpan.FromSeconds(1d / 120);

        var position = ReplayPlaybackSchedule.CatchUp(
            currentIndex: 0,
            endIndex: 20,
            currentScheduledElapsed: TimeSpan.Zero,
            actualElapsed: TimeSpan.FromMilliseconds(40),
            (_, _) => frameInterval);

        Assert.Equal(4, position.LogicalIndex);
        Assert.Equal(frameInterval * 4, position.ScheduledElapsed);
    }

    [Fact]
    public void Catch_up_never_skips_an_alarm_or_QA_pause_frame()
    {
        var frameInterval = TimeSpan.FromMilliseconds(10);

        var position = ReplayPlaybackSchedule.CatchUp(
            currentIndex: 5,
            endIndex: 20,
            currentScheduledElapsed: TimeSpan.FromMilliseconds(50),
            actualElapsed: TimeSpan.FromMilliseconds(180),
            (_, _) => frameInterval,
            stopAtIndex: 8);

        Assert.Equal(8, position.LogicalIndex);
        Assert.Equal(TimeSpan.FromMilliseconds(80), position.ScheduledElapsed);
    }

    [Fact]
    public void Catch_up_waits_when_the_next_frame_is_not_due()
    {
        var position = ReplayPlaybackSchedule.CatchUp(
            currentIndex: 3,
            endIndex: 10,
            currentScheduledElapsed: TimeSpan.FromSeconds(1),
            actualElapsed: TimeSpan.FromMilliseconds(1005),
            (_, _) => TimeSpan.FromMilliseconds(10));

        Assert.Equal(3, position.LogicalIndex);
        Assert.Equal(TimeSpan.FromSeconds(1), position.ScheduledElapsed);
    }

    [Fact]
    public void Catch_up_bounds_zero_delay_work_per_UI_tick()
    {
        var position = ReplayPlaybackSchedule.CatchUp(
            currentIndex: 0,
            endIndex: 1_000_000,
            currentScheduledElapsed: TimeSpan.Zero,
            actualElapsed: TimeSpan.FromSeconds(1),
            (_, _) => TimeSpan.Zero);

        Assert.Equal(ReplayPlaybackSchedule.DefaultMaximumAdvance, position.LogicalIndex);
        Assert.Equal(TimeSpan.Zero, position.ScheduledElapsed);
    }

    [Fact]
    public void Catch_up_rejects_a_non_positive_work_budget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplayPlaybackSchedule.CatchUp(
            currentIndex: 0,
            endIndex: 10,
            currentScheduledElapsed: TimeSpan.Zero,
            actualElapsed: TimeSpan.Zero,
            (_, _) => TimeSpan.Zero,
            maximumAdvance: 0));
    }
}
