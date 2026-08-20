using Nvt.Replay.Analysis;

namespace Nvt.Replay.Rendering;

public enum ReplayLegendPosition
{
    Auto,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public static class ReplayLegendPositioner
{
    private const double HorizontalFraction = 0.17;
    private const double HeaderFraction = 0.08;
    private const double RowFraction = 0.05;
    private const double InsetFraction = 0.015;

    public static ReplayLegendPosition Choose(
        IEnumerable<ReplayContact> contacts,
        ReplayExtent extent,
        bool reverseX = false,
        bool reverseY = false,
        bool swapAxes = false)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(extent);
        if (extent.MaximumX <= 0 || extent.MaximumY <= 0)
            throw new ArgumentOutOfRangeException(nameof(extent));

        var captured = contacts.ToArray();
        if (captured.Length == 0) return ReplayLegendPosition.TopLeft;

        var points = captured.Select(contact =>
        {
            var x = Math.Clamp(
                swapAxes ? contact.Y / extent.MaximumY : contact.X / extent.MaximumX,
                0,
                1);
            var y = Math.Clamp(
                swapAxes ? contact.X / extent.MaximumX : contact.Y / extent.MaximumY,
                0,
                1);
            return (X: reverseX ? 1 - x : x, Y: reverseY ? 1 - y : y);
        }).ToArray();
        var rowCount = Math.Min(10, captured.Select(contact => contact.Id).Distinct().Count());
        var height = Math.Clamp(HeaderFraction + (rowCount * RowFraction), 0.13, 0.58);
        var candidates = new[]
        {
            (Position: ReplayLegendPosition.TopLeft, Bounds: Bounds(ReplayLegendPosition.TopLeft, height)),
            (Position: ReplayLegendPosition.TopRight, Bounds: Bounds(ReplayLegendPosition.TopRight, height)),
            (Position: ReplayLegendPosition.BottomLeft, Bounds: Bounds(ReplayLegendPosition.BottomLeft, height)),
            (Position: ReplayLegendPosition.BottomRight, Bounds: Bounds(ReplayLegendPosition.BottomRight, height)),
        };

        return candidates
            .Select(candidate => (candidate.Position, Score: points.Sum(point => Score(candidate.Bounds, point))))
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Position)
            .First().Position;
    }

    internal static ReplayLegendPosition Choose(
        ReplayLegendOccupancySummary occupancy,
        ReplayExtent extent,
        bool reverseX = false,
        bool reverseY = false,
        bool swapAxes = false)
    {
        ArgumentNullException.ThrowIfNull(occupancy);
        ArgumentNullException.ThrowIfNull(extent);
        if (extent.MaximumX <= 0 || extent.MaximumY <= 0)
            throw new ArgumentOutOfRangeException(nameof(extent));
        if (occupancy.IsEmpty) return ReplayLegendPosition.TopLeft;

        var height = Math.Clamp(HeaderFraction + (occupancy.RowCount * RowFraction), 0.13, 0.58);
        var best = ReplayLegendPosition.TopLeft;
        var bestScore = double.PositiveInfinity;
        for (var value = (int)ReplayLegendPosition.TopLeft; value <= (int)ReplayLegendPosition.BottomRight; value++)
        {
            var position = (ReplayLegendPosition)value;
            var score = occupancy.Score(
                Bounds(position, height),
                extent,
                reverseX,
                reverseY,
                swapAxes);
            if (score < bestScore)
            {
                best = position;
                bestScore = score;
            }
        }
        return best;
    }

    private static NormalizedRect Bounds(ReplayLegendPosition position, double height)
    {
        var left = position is ReplayLegendPosition.TopLeft or ReplayLegendPosition.BottomLeft
            ? InsetFraction
            : 1 - InsetFraction - HorizontalFraction;
        var top = position is ReplayLegendPosition.TopLeft or ReplayLegendPosition.TopRight
            ? InsetFraction
            : 1 - InsetFraction - height;
        return new NormalizedRect(left, top, HorizontalFraction, height);
    }

    private static double Score(NormalizedRect bounds, (double X, double Y) point)
    {
        var deltaX = point.X < bounds.Left
            ? bounds.Left - point.X
            : point.X > bounds.Right
                ? point.X - bounds.Right
                : 0;
        var deltaY = point.Y < bounds.Top
            ? bounds.Top - point.Y
            : point.Y > bounds.Bottom
                ? point.Y - bounds.Bottom
                : 0;
        var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
        return distanceSquared < 0.000_001
            ? 10_000
            : 1 / (0.01 + distanceSquared);
    }

    internal readonly record struct NormalizedRect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
}

/// <summary>
/// Bounded occupancy projection used by Paint auto-legend placement. It retains weighted raw
/// coordinate centroids for at most 64 x 64 cells, so view transforms never enumerate the
/// capture's full contact collection again.
/// </summary>
internal sealed class ReplayLegendOccupancySummary
{
    private const int GridSize = 64;
    private readonly Cell[] cells;

    private ReplayLegendOccupancySummary(Cell[] cells, int rowCount)
    {
        this.cells = cells;
        RowCount = rowCount;
    }

    public bool IsEmpty => cells.Length == 0;
    public int RowCount { get; }

    public static ReplayLegendOccupancySummary Create(
        IEnumerable<ReplayContact> contacts,
        ReplayExtent basisExtent)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(basisExtent);
        if (basisExtent.MaximumX <= 0 || basisExtent.MaximumY <= 0)
            throw new ArgumentOutOfRangeException(nameof(basisExtent));

        var counts = new long[GridSize * GridSize];
        var sumsX = new long[counts.Length];
        var sumsY = new long[counts.Length];
        Span<ulong> ids = stackalloc ulong[4];
        foreach (var contact in contacts)
        {
            var x = Math.Clamp(contact.X / basisExtent.MaximumX, 0, 1);
            var y = Math.Clamp(contact.Y / basisExtent.MaximumY, 0, 1);
            var column = Math.Min(GridSize - 1, (int)(x * GridSize));
            var row = Math.Min(GridSize - 1, (int)(y * GridSize));
            var index = (row * GridSize) + column;
            counts[index]++;
            sumsX[index] += contact.X;
            sumsY[index] += contact.Y;
            ids[contact.Id >> 6] |= 1UL << (contact.Id & 63);
        }

        var occupiedCount = 0;
        for (var index = 0; index < counts.Length; index++)
            if (counts[index] > 0) occupiedCount++;
        var cells = new Cell[occupiedCount];
        var destination = 0;
        for (var index = 0; index < counts.Length; index++)
        {
            var count = counts[index];
            if (count == 0) continue;
            cells[destination++] = new Cell(
                sumsX[index] / (double)count,
                sumsY[index] / (double)count,
                count);
        }
        var distinctIds = 0;
        for (var index = 0; index < ids.Length; index++) distinctIds += System.Numerics.BitOperations.PopCount(ids[index]);
        return new ReplayLegendOccupancySummary(cells, Math.Min(10, distinctIds));
    }

    public double Score(
        ReplayLegendPositioner.NormalizedRect bounds,
        ReplayExtent extent,
        bool reverseX,
        bool reverseY,
        bool swapAxes)
    {
        var result = 0d;
        foreach (var cell in cells)
        {
            var x = Math.Clamp(swapAxes ? cell.Y / extent.MaximumY : cell.X / extent.MaximumX, 0, 1);
            var y = Math.Clamp(swapAxes ? cell.X / extent.MaximumX : cell.Y / extent.MaximumY, 0, 1);
            if (reverseX) x = 1 - x;
            if (reverseY) y = 1 - y;

            var deltaX = x < bounds.Left
                ? bounds.Left - x
                : x > bounds.Right
                    ? x - bounds.Right
                    : 0;
            var deltaY = y < bounds.Top
                ? bounds.Top - y
                : y > bounds.Bottom
                    ? y - bounds.Bottom
                    : 0;
            var distanceSquared = (deltaX * deltaX) + (deltaY * deltaY);
            var score = distanceSquared < 0.000_001
                ? 10_000
                : 1 / (0.01 + distanceSquared);
            result += score * cell.Count;
        }
        return result;
    }

    private readonly record struct Cell(double X, double Y, long Count);
}
