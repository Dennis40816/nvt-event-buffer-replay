// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class DetailPresentationThrottleTests
{
    private static long Ms(int milliseconds) => milliseconds * TimeProvider.System.TimestampFrequency / 1000;

    [Fact]
    public void FirstRequestIsAcceptedAndRecorded()
    {
        var now = Ms(1000);
        var throttle = new DetailPresentationThrottle(() => now);
        var interval = TimeSpan.FromMilliseconds(100);

        Assert.True(throttle.TryAcquire(interval));
        now = Ms(1050);
        Assert.False(throttle.TryAcquire(interval));
        now = Ms(1100);
        Assert.True(throttle.TryAcquire(interval));
    }

    [Fact]
    public void MarkPresentedCountsAndRestartsTheInterval()
    {
        var now = Ms(2000);
        var throttle = new DetailPresentationThrottle(() => now);
        var interval = TimeSpan.FromMilliseconds(100);

        throttle.MarkPresented();
        now = Ms(2099);

        Assert.Equal(1, throttle.Count);
        Assert.False(throttle.TryAcquire(interval));
        now = Ms(2100);
        Assert.True(throttle.TryAcquire(interval));
        Assert.Equal(1, throttle.Count);
    }

    [Fact]
    public void ResetClearsTheCountAndTheInterval()
    {
        var now = Ms(3000);
        var throttle = new DetailPresentationThrottle(() => now);
        var interval = TimeSpan.FromMilliseconds(100);
        throttle.MarkPresented();
        throttle.MarkPresented();

        throttle.Reset();

        Assert.Equal(0, throttle.Count);
        Assert.True(throttle.TryAcquire(interval));
    }

    [Fact]
    public void DefaultClockIsTheSystemClock()
    {
        var throttle = new DetailPresentationThrottle();

        Assert.True(throttle.TryAcquire(TimeSpan.FromHours(1)));
        Assert.False(throttle.TryAcquire(TimeSpan.FromHours(1)));
    }
}
