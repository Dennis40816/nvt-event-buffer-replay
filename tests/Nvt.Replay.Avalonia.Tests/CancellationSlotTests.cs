// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class CancellationSlotTests
{
    [Fact]
    public void NewSlotIsNotActive()
    {
        var slot = new CancellationSlot();

        Assert.False(slot.IsActive);
        Assert.Null(slot.Current);
    }

    [Fact]
    public void BeginCancelsAndDisposesThePreviousSource()
    {
        var slot = new CancellationSlot();
        var first = slot.Begin();
        var firstToken = first.Token;

        var second = slot.Begin();

        Assert.True(firstToken.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.Same(second, slot.Current);
        Assert.Throws<ObjectDisposedException>(() => first.Cancel());
    }

    [Fact]
    public void CancelCurrentCancelsAndKeepsTheSourceInstalled()
    {
        var slot = new CancellationSlot();
        var source = slot.Begin();

        slot.CancelCurrent();

        Assert.True(source.IsCancellationRequested);
        Assert.Same(source, slot.Current);
        Assert.True(slot.IsActive);
    }

    [Fact]
    public void TakeDetachesWithoutCancelling()
    {
        var slot = new CancellationSlot();
        var source = slot.Begin();

        var taken = slot.Take();

        Assert.Same(source, taken);
        Assert.False(slot.IsActive);
        Assert.False(source.IsCancellationRequested);
        source.Dispose();
    }

    [Fact]
    public void CancelAndClearCancelsDisposesAndEmptiesTheSlot()
    {
        var slot = new CancellationSlot();
        var source = slot.Begin();
        var token = source.Token;

        slot.CancelAndClear();

        Assert.True(token.IsCancellationRequested);
        Assert.False(slot.IsActive);
        Assert.Throws<ObjectDisposedException>(() => source.Cancel());
    }

    [Fact]
    public void ReleaseIfCurrentIgnoresASupersededSource()
    {
        var slot = new CancellationSlot();
        using var superseded = new CancellationTokenSource();
        slot.Assign(superseded);
        var current = slot.Begin();

        var released = slot.ReleaseIfCurrent(superseded);

        Assert.False(released);
        Assert.Same(current, slot.Current);
    }

    [Fact]
    public void ReleaseIfCurrentDetachesAndDisposesTheCurrentSource()
    {
        var slot = new CancellationSlot();
        var current = slot.Begin();

        var released = slot.ReleaseIfCurrent(current);

        Assert.True(released);
        Assert.False(slot.IsActive);
        Assert.Throws<ObjectDisposedException>(() => current.Cancel());
    }
}
