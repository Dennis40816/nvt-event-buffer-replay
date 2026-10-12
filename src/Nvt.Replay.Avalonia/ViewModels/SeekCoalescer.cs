// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Pending continuous seek and drain scheduling, owned by the UI thread.</summary>
internal sealed class SeekCoalescer
{
    private int? _pendingLogicalIndex;
    private bool _drainScheduled;

    internal bool Request(int logicalIndex)
    {
        _pendingLogicalIndex = logicalIndex;
        if (_drainScheduled) return false;
        _drainScheduled = true;
        return true;
    }

    internal void Cancel() => _pendingLogicalIndex = null;

    internal int? Drain(bool canSeek)
    {
        _drainScheduled = false;
        if (!canSeek) return null;
        var logicalIndex = _pendingLogicalIndex;
        _pendingLogicalIndex = null;
        return logicalIndex;
    }
}
