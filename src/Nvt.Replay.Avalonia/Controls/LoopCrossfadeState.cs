using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>The outgoing scene, trail batches and loop transition timing. UI thread only.</summary>
/// <param name="readTimestamp">Reads a system-frequency timestamp; defaults to the system clock.</param>
internal sealed class LoopCrossfadeState(Func<long>? readTimestamp = null)
{
    private static readonly TimeSpan _loopCrossfadeDuration = TimeSpan.FromMilliseconds(140);
    private readonly Func<long> _readTimestamp = readTimestamp ?? TimeProvider.System.GetTimestamp;
    private long _started;

    internal ReplayScene? OutgoingScene { get; private set; }

    internal IReadOnlyList<ReplayTrailDrawBatch> OutgoingTrailBatches { get; private set; } = [];

    internal void Begin(ReplayScene outgoing, IReadOnlyList<ReplayTrailDrawBatch> batches)
    {
        OutgoingScene = outgoing;
        OutgoingTrailBatches = batches;
        _started = _readTimestamp();
    }

    internal void End()
    {
        OutgoingScene = null;
        OutgoingTrailBatches = [];
    }

    internal double Progress()
    {
        if (OutgoingScene is null || _started == 0) return 1;
        var now = _readTimestamp();
        return Math.Clamp(
            TimeProvider.System.GetElapsedTime(_started, now).TotalMilliseconds / _loopCrossfadeDuration.TotalMilliseconds,
            0,
            1);
    }
}
