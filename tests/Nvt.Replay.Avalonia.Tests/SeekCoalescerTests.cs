// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class SeekCoalescerTests
{
    [Fact]
    public void FirstRequestSchedulesDrain()
    {
        var coalescer = new SeekCoalescer();

        var shouldSchedule = coalescer.Request(10);

        Assert.True(shouldSchedule);
    }

    [Fact]
    public void RequestWhileDrainScheduledDoesNotScheduleAgain()
    {
        var coalescer = new SeekCoalescer();
        coalescer.Request(10);

        var shouldSchedule = coalescer.Request(20);

        Assert.False(shouldSchedule);
    }

    [Fact]
    public void DrainReturnsLatestPendingIndexOnce()
    {
        var coalescer = new SeekCoalescer();
        coalescer.Request(10);
        coalescer.Request(20);

        var logicalIndex = coalescer.Drain(canSeek: true);
        var nextLogicalIndex = coalescer.Drain(canSeek: true);

        Assert.Equal(20, logicalIndex);
        Assert.Null(nextLogicalIndex);
    }

    [Fact]
    public void CancelClearsPendingIndex()
    {
        var coalescer = new SeekCoalescer();
        coalescer.Request(10);

        coalescer.Cancel();

        Assert.Null(coalescer.Drain(canSeek: true));
    }

    [Fact]
    public void CancelKeepsDrainScheduled()
    {
        var coalescer = new SeekCoalescer();
        coalescer.Request(10);

        coalescer.Cancel();
        var shouldSchedule = coalescer.Request(20);

        Assert.False(shouldSchedule);
    }

    [Fact]
    public void RequestAfterDrainSchedulesAgain()
    {
        var coalescer = new SeekCoalescer();
        coalescer.Request(10);
        coalescer.Drain(canSeek: true);

        var shouldSchedule = coalescer.Request(20);

        Assert.True(shouldSchedule);
    }

    [Fact]
    public void DrainWhenSeekUnavailablePreservesPendingIndex()
    {
        var coalescer = new SeekCoalescer();
        coalescer.Request(10);

        var unavailableLogicalIndex = coalescer.Drain(canSeek: false);
        var availableLogicalIndex = coalescer.Drain(canSeek: true);

        Assert.Null(unavailableLogicalIndex);
        Assert.Equal(10, availableLogicalIndex);
    }

    [Fact]
    public void DrainWhenSeekUnavailableAllowsSchedulingAgain()
    {
        var coalescer = new SeekCoalescer();
        coalescer.Request(10);
        coalescer.Drain(canSeek: false);

        var shouldSchedule = coalescer.Request(20);

        Assert.True(shouldSchedule);
    }
}
