// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Holds the cancellation source of one long-lived UI activity. UI thread only.</summary>
internal sealed class CancellationSlot
{
    internal CancellationTokenSource? Current { get; private set; }

    internal bool IsActive => Current is not null;

    /// <summary>Cancels and disposes the current source, then installs and returns a new one.</summary>
    internal CancellationTokenSource Begin()
    {
        Current?.Cancel();
        Current?.Dispose();
        Current = new CancellationTokenSource();
        return Current;
    }

    /// <summary>Installs a source the caller created. The caller owns the previous one.</summary>
    internal void Assign(CancellationTokenSource source) => Current = source;

    /// <summary>Cancels the current source and keeps it installed.</summary>
    internal void CancelCurrent() => Current?.Cancel();

    /// <summary>Detaches the current source without cancelling or disposing it.</summary>
    internal CancellationTokenSource? Take()
    {
        var current = Current;
        Current = null;
        return current;
    }

    /// <summary>Detaches, cancels and disposes the current source.</summary>
    internal void CancelAndClear()
    {
        Current?.Cancel();
        Current?.Dispose();
        Current = null;
    }

    /// <summary>When <paramref name="source" /> is still current, detaches and disposes it and returns true.</summary>
    internal bool ReleaseIfCurrent(CancellationTokenSource source)
    {
        if (!ReferenceEquals(Current, source)) return false;
        Current = null;
        source.Dispose();
        return true;
    }
}
