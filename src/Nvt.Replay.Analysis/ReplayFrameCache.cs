namespace Nvt.Replay.Analysis;

/// <summary>Stores replay snapshots for access by logical index.</summary>
public sealed class ReplayFrameCache
{
    private readonly ITouchReplaySnapshot[] snapshots;

    private ReplayFrameCache(ITouchReplaySnapshot[] snapshots) => this.snapshots = snapshots;

    /// <summary>Gets the number of cached replay snapshots.</summary>
    public int Count => snapshots.Length;
    /// <summary>Gets the cached replay snapshots as a read-only list.</summary>
    public IReadOnlyList<ITouchReplaySnapshot> Snapshots => snapshots;
    /// <summary>Gets the cached replay snapshot at the specified logical index.</summary>
    public ITouchReplaySnapshot this[int logicalIndex] => snapshots[logicalIndex];

    /// <summary>Caches all snapshots enumerated from the replay session using the supplied cancellation token.</summary>
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
