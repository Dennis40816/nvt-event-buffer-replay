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

    private readonly record struct NormalizedRect(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }
}
