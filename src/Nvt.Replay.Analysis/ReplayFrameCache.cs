namespace Nvt.Replay.Analysis;

public sealed class ReplayFrameCache
{
    private readonly ITouchReplaySnapshot[] snapshots;

    private ReplayFrameCache(ITouchReplaySnapshot[] snapshots) => this.snapshots = snapshots;

    public int Count => snapshots.Length;
    public IReadOnlyList<ITouchReplaySnapshot> Snapshots => snapshots;
    public ITouchReplaySnapshot this[int logicalIndex] => snapshots[logicalIndex];

    public static ReplayFrameCache Create(ITouchReplaySession replay)
    {
        ArgumentNullException.ThrowIfNull(replay);
        var snapshots = new ITouchReplaySnapshot[replay.Count];
        for (var index = 0; index < replay.Count; index++) snapshots[index] = replay.Seek(index);
        return new ReplayFrameCache(snapshots);
    }
}
