using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class LoopCrossfadeStateTests
{
    [Fact]
    public void InitialStateHasNoOutgoingContentAndFullProgress()
    {
        var state = new LoopCrossfadeState();

        Assert.Null(state.OutgoingScene);
        Assert.Empty(state.OutgoingTrailBatches);
        Assert.Equal(1, state.Progress());
    }

    [Fact]
    public void BeginKeepsOutgoingSceneAndBatchesAndStartsAtZeroProgress()
    {
        var now = Milliseconds(1000);
        var state = new LoopCrossfadeState(() => now);
        var scene = Scene();
        IReadOnlyList<ReplayTrailDrawBatch> batches = [new(1, [], [])];

        state.Begin(scene, batches);

        Assert.Same(scene, state.OutgoingScene);
        Assert.Same(batches, state.OutgoingTrailBatches);
        Assert.Equal(0, state.Progress());
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(35, 0.25)]
    [InlineData(70, 0.5)]
    [InlineData(139, 139d / 140)]
    [InlineData(140, 1)]
    [InlineData(141, 1)]
    public void ProgressUsesInjectedElapsedTimeAndClamps(int elapsedMilliseconds, double expected)
    {
        var now = Milliseconds(1000);
        var state = new LoopCrossfadeState(() => now);
        state.Begin(Scene(), []);

        now += Milliseconds(elapsedMilliseconds);

        Assert.Equal(expected, state.Progress(), precision: 12);
    }

    [Fact]
    public void ZeroStartTimestampKeepsProgressCompleteAfterClockAdvances()
    {
        long now = 0;
        var state = new LoopCrossfadeState(() => now);
        state.Begin(Scene(), []);

        now = Milliseconds(70);

        Assert.NotNull(state.OutgoingScene);
        Assert.Equal(1, state.Progress());
    }

    [Fact]
    public void InactiveProgressDoesNotReadTheClock()
    {
        var now = Milliseconds(1000);
        var reads = 0;
        var state = new LoopCrossfadeState(() => { reads++; return now; });

        var progress = state.Progress();

        Assert.Equal(1, progress);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void EndClearsOutgoingSceneAndBatchesWithoutReadingTheClock()
    {
        var now = Milliseconds(1000);
        var reads = 0;
        var state = new LoopCrossfadeState(() => { reads++; return now; });
        state.Begin(Scene(), [new(1, [], [])]);

        state.End();

        Assert.Null(state.OutgoingScene);
        Assert.Empty(state.OutgoingTrailBatches);
        Assert.Equal(1, state.Progress());
        Assert.Equal(1, reads);
    }

    [Fact]
    public void BeginDuringCrossfadeReplacesOutgoingContentAndRestartsProgress()
    {
        var now = Milliseconds(1000);
        var state = new LoopCrossfadeState(() => now);
        state.Begin(Scene(), [new(1, [], [])]);
        now += Milliseconds(70);
        var next = Scene() with { LogicalIndex = 1 };
        IReadOnlyList<ReplayTrailDrawBatch> batches = [new(2, [], [])];

        state.Begin(next, batches);

        Assert.Same(next, state.OutgoingScene);
        Assert.Same(batches, state.OutgoingTrailBatches);
        Assert.Equal(0, state.Progress());
    }

    [Fact]
    public void BeginAfterEndUsesTheNewStartTimestamp()
    {
        var now = Milliseconds(1000);
        var state = new LoopCrossfadeState(() => now);
        state.Begin(Scene(), []);
        state.End();
        now += Milliseconds(200);

        state.Begin(Scene(), []);
        now += Milliseconds(70);

        Assert.Equal(0.5, state.Progress());
    }

    private static long Milliseconds(int value) => value * TimeProvider.System.TimestampFrequency / 1000;

    private static ReplayScene Scene() => new(
        0,
        2,
        new ReplayTimelineEntry(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(1), false, false),
        [],
        [],
        false,
        new ReplayExtent(640, 480),
        [],
        [],
        [],
        false,
        false,
        false);
}
