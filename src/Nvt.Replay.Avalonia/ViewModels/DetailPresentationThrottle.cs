// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Limits how often playback refreshes the detail panel and counts the refreshes.</summary>
/// <param name="readTimestamp">Reads a system timestamp. Defaults to <see cref="TimeProvider.System"/>.</param>
internal sealed class DetailPresentationThrottle(Func<long>? readTimestamp = null)
{
    private readonly Func<long> _readTimestamp = readTimestamp ?? TimeProvider.System.GetTimestamp;
    private long _lastTimestamp;

    /// <summary>The number of presentations since the last reset.</summary>
    internal int Count { get; private set; }

    /// <summary>True when enough time has passed since the last presentation. Restarts the interval then.</summary>
    internal bool TryAcquire(TimeSpan interval)
    {
        var now = _readTimestamp();
        if (_lastTimestamp != 0 && TimeProvider.System.GetElapsedTime(_lastTimestamp, now) < interval) return false;
        _lastTimestamp = now;
        return true;
    }

    /// <summary>Counts a presentation and restarts the interval.</summary>
    internal void MarkPresented()
    {
        _lastTimestamp = _readTimestamp();
        Count++;
    }

    internal void Reset()
    {
        _lastTimestamp = 0;
        Count = 0;
    }
}
