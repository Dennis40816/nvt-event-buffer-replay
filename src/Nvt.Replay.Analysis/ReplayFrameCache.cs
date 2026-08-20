namespace Nvt.Replay.Analysis;

public sealed class ReplayFrameCache
{
    private readonly ITouchReplaySnapshot[] snapshots;

    private ReplayFrameCache(ITouchReplaySnapshot[] snapshots) => this.snapshots = snapshots;

    public int Count => snapshots.Length;
    public IReadOnlyList<ITouchReplaySnapshot> Snapshots => snapshots;
    public ITouchReplaySnapshot this[int logicalIndex] => snapshots[logicalIndex];

    public static ReplayFrameCache Create(
        ITouchReplaySession replay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replay);
        return new ReplayFrameCache(replay.EnumerateSnapshots(cancellationToken).ToArray());
    }

    internal static ReplayFrameCache FromOwnedSnapshots(ITouchReplaySnapshot[] snapshots) =>
        new(snapshots);
}
