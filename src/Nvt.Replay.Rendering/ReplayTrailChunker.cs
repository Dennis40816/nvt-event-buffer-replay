namespace Nvt.Replay.Rendering;

/// <summary>Represents a range of replay trail points with an optional leading point and cache eligibility.</summary>
/// <param name="Source">The point collection containing the chunk and any leading point.</param>
/// <param name="Offset">The zero-based index in Source of the first point in the chunk.</param>
/// <param name="Count">The number of points in the chunk, excluding any leading point.</param>
/// <param name="LeadingOffset">The optional index in Source of the point preceding the chunk.</param>
/// <param name="IsCacheable">Whether the chunk is full or belongs to a completed trail and is eligible for caching.</param>
public readonly record struct ReplayTrailChunk(
    IReadOnlyList<ReplayTrailPoint> Source,
    int Offset,
    int Count,
    int? LeadingOffset,
    bool IsCacheable)
{
    /// <summary>Gets the trail point at a zero-based index within this chunk.</summary>
    public ReplayTrailPoint this[int index] =>
        index >= 0 && index < Count
            ? Source[Offset + index]
            : throw new ArgumentOutOfRangeException(nameof(index));

    /// <summary>Gets the source point at the leading offset, or throws when no leading offset is set.</summary>
    public ReplayTrailPoint LeadingPoint => LeadingOffset is { } offset
        ? Source[offset]
        : throw new InvalidOperationException("This chunk has no leading point.");
}

/// <summary>Splits replay contact trails into chunks that reference their source points.</summary>
public static class ReplayTrailChunker
{
    /// <summary>The default maximum number of trail points per chunk, excluding the leading point.</summary>
    public const int DefaultChunkSize = 2048;

    /// <summary>Enumerates chunks of the trail's visible points, with a preceding point for each chunk after the first.</summary>
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
