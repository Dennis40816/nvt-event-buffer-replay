using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class ReplayPlaybackControllerTests
{
    [Fact]
    public void Timing_supports_point_zero_one_normal_and_maximum_rates()
    {
        var controller = Controller(frameCount: 101, frameInterval: TimeSpan.FromMilliseconds(10));
        controller.Seek(0);
        controller.ConfigureTiming(
            ReplayPlaybackClockDomain.Frame,
            compressIdle: true,
            ReplayPlaybackRate.FromMultiplier(0.01));

        var slow = controller.Play();
        Assert.Equal(TimeSpan.FromSeconds(1), controller.DelayBetween(0, 1));
        Assert.Equal(TimeSpan.FromSeconds(1), controller.PlanNext(slow.Generation).DueTime);
        controller.Pause();

        controller.ConfigureTiming(
            ReplayPlaybackClockDomain.Frame,
            compressIdle: true,
            ReplayPlaybackRate.Normal);
        var normal = controller.Play();
        Assert.Equal(TimeSpan.FromMilliseconds(10), controller.DelayBetween(0, 1));
        Assert.Equal(ReplayPlaybackController.MinimumVisualInterval, controller.PlanNext(normal.Generation).DueTime);
        controller.Pause();

        controller.ConfigureTiming(
            ReplayPlaybackClockDomain.Frame,
            compressIdle: true,
            ReplayPlaybackRate.Maximum);
        var maximum = controller.Play();
        var operation = controller.Advance(maximum.Generation, ReplayPlaybackController.MinimumVisualInterval);

        Assert.Equal(ReplayPlaybackOperationKind.Move, operation.Kind);
        Assert.Equal(50, operation.LogicalIndex);
    }

    [Fact]
    public void Clock_domain_and_idle_compression_select_the_expected_timeline()
    {
        var timeline = new[]
        {
            Entry(0, recordedMs: 0, frameMs: 0, displayMs: 0),
            Entry(1, recordedMs: 1_000, frameMs: 10, displayMs: 100),
        };
        var controller = new ReplayPlaybackController();
        controller.Load(timeline);

        controller.ConfigureTiming(ReplayPlaybackClockDomain.Recorded, false, ReplayPlaybackRate.Normal);
        Assert.Equal(TimeSpan.FromSeconds(1), controller.DelayBetween(0, 1));

        controller.ConfigureTiming(ReplayPlaybackClockDomain.Recorded, true, ReplayPlaybackRate.Normal);
        Assert.Equal(TimeSpan.FromMilliseconds(100), controller.DelayBetween(0, 1));

        controller.ConfigureTiming(ReplayPlaybackClockDomain.Frame, true, ReplayPlaybackRate.Normal);
        Assert.Equal(TimeSpan.FromMilliseconds(10), controller.DelayBetween(0, 1));
    }

    [Fact]
    public void Loop_restarts_at_inclusive_boundary_and_rejects_a_single_frame_range()
    {
        var controller = Controller(5);
        controller.ConfigureLoop(true, 1, 3);
        controller.Seek(3);

        var start = controller.Play();
        var restart = controller.PlanNext(start.Generation);

        Assert.Equal(ReplayPlaybackOperationKind.LoopRestart, restart.Kind);
        Assert.Equal(1, restart.LogicalIndex);

        controller.Pause();
        controller.ConfigureLoop(true, 2, 2);
        var invalid = controller.Play();
        Assert.Equal(ReplayPlaybackStartKind.InvalidLoop, invalid.Kind);
        Assert.False(controller.IsPlaying);
    }

    [Theory]
    [InlineData(true, false, 2, ReplayPlaybackPauseReason.Break)]
    [InlineData(false, true, 3, ReplayPlaybackPauseReason.AllBreak)]
    public void Automatic_pause_stops_on_the_first_enabled_event(
        bool pauseOnBreak,
        bool pauseOnAllBreak,
        int expectedIndex,
        ReplayPlaybackPauseReason expectedReason)
    {
        var snapshots = Enumerable.Range(0, 5)
            .Select(index => Snapshot(index, hasBreak: index == 2, allBreak: index == 3))
            .ToArray();
        var controller = new ReplayPlaybackController();
        controller.Load(snapshots.Select(item => item.Timeline).ToArray(), ReplayAutoPauseIndex.Create(snapshots));
        controller.ConfigureAutoPause(new ReplayPlaybackPauseOptions(pauseOnBreak, pauseOnAllBreak));
        controller.Seek(0);

        var start = controller.Play();
        var operation = controller.Advance(start.Generation, TimeSpan.FromSeconds(1));

        Assert.Equal(ReplayPlaybackOperationKind.Pause, operation.Kind);
        Assert.Equal(expectedIndex, operation.LogicalIndex);
        Assert.Equal(expectedReason, operation.PauseDecision?.Reason);
        Assert.False(controller.IsPlaying);
    }

    [Fact]
    public void Review_pause_wins_a_tie_with_an_automatic_pause()
    {
        var snapshots = Enumerable.Range(0, 4)
            .Select(index => Snapshot(index, hasBreak: index == 2, allBreak: false))
            .ToArray();
        var controller = new ReplayPlaybackController();
        controller.Load(
            snapshots.Select(item => item.Timeline).ToArray(),
            ReplayAutoPauseIndex.Create(snapshots),
            (_, _) => 2);
        controller.ConfigureAutoPause(new ReplayPlaybackPauseOptions(true, false));
        controller.Seek(0);

        var start = controller.Play();
        var operation = controller.Advance(start.Generation, TimeSpan.FromSeconds(1));

        Assert.Equal(ReplayPlaybackPauseReason.Review, operation.PauseDecision?.Reason);
        Assert.True(operation.PauseDecision?.SelectReview);
    }

    [Fact]
    public void Timing_switch_invalidates_the_old_generation_without_creating_overlapping_ticks()
    {
        var controller = Controller(200);
        controller.Seek(0);
        var first = controller.Play();

        var change = controller.ConfigureTiming(
            ReplayPlaybackClockDomain.Frame,
            true,
            ReplayPlaybackRate.FromMultiplier(0.01));

        Assert.True(change.Changed);
        Assert.True(change.RestartRequired);
        Assert.NotEqual(first.Generation, change.Generation);
        Assert.Equal(ReplayPlaybackOperationKind.Stale, controller.PlanNext(first.Generation).Kind);
        Assert.Equal(ReplayPlaybackOperationKind.Wait, controller.PlanNext(change.Generation).Kind);

        var unchanged = controller.ConfigureTiming(
            ReplayPlaybackClockDomain.Frame,
            true,
            ReplayPlaybackRate.FromMultiplier(0.01));
        Assert.False(unchanged.Changed);
        Assert.False(unchanged.RestartRequired);
        Assert.Equal(change.Generation, unchanged.Generation);
    }

    [Fact]
    public void Step_pauses_and_clamps_the_current_index()
    {
        var controller = Controller(3);
        controller.Seek(1);
        controller.Play();

        Assert.Equal(2, controller.Step(1));
        Assert.False(controller.IsPlaying);
        Assert.Equal(2, controller.Step(10));
        Assert.Equal(0, controller.Step(-10));
    }

    private static ReplayPlaybackController Controller(
        int frameCount,
        TimeSpan? frameInterval = null)
    {
        var interval = frameInterval ?? TimeSpan.FromMilliseconds(10);
        var controller = new ReplayPlaybackController();
        controller.Load(Enumerable.Range(0, frameCount)
            .Select(index => Entry(
                index,
                interval.TotalMilliseconds * index,
                interval.TotalMilliseconds * index,
                interval.TotalMilliseconds * index))
            .ToArray());
        return controller;
    }

    private static ReplayTimelineEntry Entry(
        int index,
        double recordedMs,
        double frameMs,
        double displayMs) =>
        new(
            index,
            TimeSpan.FromMilliseconds(recordedMs),
            TimeSpan.FromMilliseconds(frameMs),
            TimeSpan.FromMilliseconds(displayMs),
            index == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(recordedMs),
            false,
            false);

    private static FakeSnapshot Snapshot(int index, bool hasBreak, bool allBreak)
    {
        var source = new SourceRecord(
            index,
            $"source-{index}",
            DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(index * 10),
            BusOperation.Paint,
            "TP",
            1,
            80,
            [],
            string.Empty,
            new SourceLocation(index, index + 1));
        IReadOnlyList<ReplayContact> contacts = hasBreak
            ? [new ReplayContact(1, TouchType.Finger, TouchStatus.Break, 10, 20, source.StableId)]
            : [];
        return new FakeSnapshot(
            index,
            source,
            Entry(index, index * 10, index * 10, index * 10),
            contacts,
            allBreak);
    }

    private sealed record FakeSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        ReplayTimelineEntry Timeline,
        IReadOnlyList<ReplayContact> ReportedContacts,
        bool AllBreak) : ITouchReplaySnapshot
    {
        public object DecodedFrame => this;
        public IReadOnlyList<SourceRecord> PhysicalRecords => [PrimarySource];
        public IReadOnlyList<ReplayContact> HostContacts => ReportedContacts;
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }
}
