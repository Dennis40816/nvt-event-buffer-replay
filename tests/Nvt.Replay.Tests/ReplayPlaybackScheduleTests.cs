using Nvt.Replay.Analysis;

namespace Nvt.Replay.Tests;

public sealed class ReplayPlaybackScheduleTests
{
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
}
