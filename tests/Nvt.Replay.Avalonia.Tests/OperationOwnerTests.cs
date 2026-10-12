// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class OperationOwnerTests
{
    [Fact]
    public void BeginCancelsPreviousLease()
    {
        using var owner = new OperationOwner();
        var previous = owner.Begin();

        var current = owner.Begin();

        Assert.True(previous.Token.IsCancellationRequested);
        Assert.False(current.Token.IsCancellationRequested);
        Assert.True(current.Id > previous.Id);
        Assert.False(owner.IsCurrent(previous));
        Assert.True(owner.IsCurrent(current));
    }

    [Fact]
    public void CancellationCallbackSeesNewLeaseAsCurrent()
    {
        using var owner = new OperationOwner();
        var previous = owner.Begin();
        var oldWasCurrent = true;
        var oldReleased = true;
        using var registration = previous.Token.Register(() =>
        {
            oldWasCurrent = owner.IsCurrent(previous);
            oldReleased = owner.Release(previous);
        });

        var current = owner.Begin();

        Assert.False(oldWasCurrent);
        Assert.False(oldReleased);
        Assert.True(owner.IsCurrent(current));
        Assert.False(current.Token.IsCancellationRequested);
    }

    [Fact]
    public void StaleReleaseLeavesCurrentLeaseActive()
    {
        using var owner = new OperationOwner();
        var previous = owner.Begin();
        var current = owner.Begin();

        var released = owner.Release(previous);

        Assert.False(released);
        Assert.True(owner.Active);
        Assert.True(owner.IsCurrent(current));
        Assert.False(current.Token.IsCancellationRequested);
    }

    [Fact]
    public void CurrentReleaseEndsOwnership()
    {
        using var owner = new OperationOwner();
        var current = owner.Begin();

        var released = owner.Release(current);

        Assert.True(released);
        Assert.False(owner.Active);
        Assert.False(owner.IsCurrent(current));
        Assert.False(owner.Release(current));
    }

    [Fact]
    public void ActiveIsFalseBeforeBegin()
    {
        using var owner = new OperationOwner();

        Assert.False(owner.Active);
    }

    [Fact]
    public void BeginAfterReleaseAdvancesId()
    {
        using var owner = new OperationOwner();
        var previous = owner.Begin();
        owner.Release(previous);

        var current = owner.Begin();

        Assert.True(current.Id > previous.Id);
        Assert.False(previous.Token.IsCancellationRequested);
        Assert.True(owner.Active);
    }

    [Fact]
    public void IsCurrentRejectsAnotherOwnersLease()
    {
        using var owner = new OperationOwner();
        using var other = new OperationOwner();
        var current = owner.Begin();
        var foreign = other.Begin();

        Assert.False(owner.IsCurrent(foreign));
        Assert.False(owner.Release(foreign));
        Assert.True(owner.IsCurrent(current));
    }

    [Fact]
    public void CancelCurrentCancelsTokenAndRetainsOwnership()
    {
        using var owner = new OperationOwner();
        var current = owner.Begin();
        var cancellations = 0;
        using var registration = current.Token.Register(() => cancellations++);

        owner.CancelCurrent();
        owner.CancelCurrent();

        Assert.True(current.Token.IsCancellationRequested);
        Assert.True(owner.Active);
        Assert.True(owner.IsCurrent(current));
        Assert.Equal(1, cancellations);
    }

    [Fact]
    public void CancelCurrentWithoutLeaseDoesNothing()
    {
        using var owner = new OperationOwner();

        owner.CancelCurrent();

        Assert.False(owner.Active);
    }

    [Fact]
    public void CancelCurrentAfterReleaseDoesNothing()
    {
        using var owner = new OperationOwner();
        var current = owner.Begin();
        owner.Release(current);

        owner.CancelCurrent();

        Assert.False(owner.Active);
        Assert.False(current.Token.IsCancellationRequested);
    }
}
