namespace Nvt.Replay.Rendering;

public readonly record struct ReplayTrailChunk(
    IReadOnlyList<ReplayTrailPoint> Source,
    int Offset,
    int Count,
    int? LeadingOffset,
    bool IsCacheable)
{
    public ReplayTrailPoint this[int index] =>
        index >= 0 && index < Count
            ? Source[Offset + index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    public ReplayTrailPoint LeadingPoint => LeadingOffset is { } offset
        ? Source[offset]
        : throw new InvalidOperationException("This chunk has no leading point.");
}

public static class ReplayTrailChunker
{
    public const int DefaultChunkSize = 2048;

    public static IEnumerable<ReplayTrailChunk> Enumerate(
        ReplayContactTrail trail,
        int chunkSize = DefaultChunkSize)
    {
        ArgumentNullException.ThrowIfNull(trail);
        if (chunkSize < 2) throw new ArgumentOutOfRangeException(nameof(chunkSize));

        var (source, sourceOffset, visibleCount) = Normalize(trail.Points);
        for (var relativeOffset = 0; relativeOffset < visibleCount; relativeOffset += chunkSize)
        {
            var count = Math.Min(chunkSize, visibleCount - relativeOffset);
            var offset = sourceOffset + relativeOffset;
            yield return new ReplayTrailChunk(
                source,
                offset,
                count,
                relativeOffset == 0 ? null : offset - 1,
                count == chunkSize || trail.IsComplete);
        }
    }

    private static (IReadOnlyList<ReplayTrailPoint> Source, int Offset, int Count) Normalize(
        IReadOnlyList<ReplayTrailPoint> points)
    {
        if (points is ArraySegment<ReplayTrailPoint> segment && segment.Array is { } array)
            return (array, segment.Offset, segment.Count);
        return (points, 0, points.Count);
    }
}
