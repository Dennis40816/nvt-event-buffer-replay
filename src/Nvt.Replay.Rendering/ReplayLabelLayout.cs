namespace Nvt.Replay.Rendering;

public readonly record struct ReplayLabelBounds(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool Intersects(ReplayLabelBounds other, double gap = 0) =>
        X < other.Right + gap && Right + gap > other.X &&
        Y < other.Bottom + gap && Bottom + gap > other.Y;
}

public readonly record struct ReplayLabelRequest(
    int Key,
    double AnchorX,
    double AnchorY,
    double Width,
    double Height);

public readonly record struct ReplayLabelPlacement(
    int Key,
    ReplayLabelBounds Bounds,
    double LeaderX,
    double LeaderY);

/// <summary>
/// Deterministic contact-label placement shared by the interactive canvas and
/// raster/video renderer. Labels never overlap one another when a valid slot
/// exists inside the supplied bounds, and avoid every contact point.
/// </summary>
public static class ReplayLabelLayout
{
    public static IReadOnlyList<ReplayLabelPlacement> Place(
        ReplayLabelBounds bounds,
        IReadOnlyList<ReplayLabelRequest> requests,
        double pointRadius = 23,
        double clearance = 27,
        double labelGap = 5)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (bounds.Width <= 0 || bounds.Height <= 0 || requests.Count == 0)
            return [];

        var protectedPoints = requests
            .Select(request => new ReplayLabelBounds(
                request.AnchorX - pointRadius,
                request.AnchorY - pointRadius,
                pointRadius * 2,
                pointRadius * 2))
            .ToArray();
        var occupied = new List<ReplayLabelBounds>(requests.Count);
        var placements = new List<ReplayLabelPlacement>(requests.Count);

        foreach (var request in requests)
        {
            var candidate = NearbyCandidates(bounds, request, clearance)
                .FirstOrDefault(value => IsFree(value, protectedPoints, occupied, labelGap));

            if (candidate.Width <= 0)
            {
                candidate = FallbackCandidates(bounds, request, labelGap)
                    .FirstOrDefault(value => IsFree(value, protectedPoints, occupied, labelGap));
            }

            // A tiny viewport can be physically incapable of fitting every
            // label. Keep the result deterministic and inside the viewport;
            // normal replay/export sizes reach the hard non-overlap paths above.
            if (candidate.Width <= 0)
                candidate = BestEffortCandidate(bounds, request, protectedPoints, occupied);

            occupied.Add(candidate);
            placements.Add(new ReplayLabelPlacement(
                request.Key,
                candidate,
                Math.Clamp(request.AnchorX, candidate.X, candidate.Right),
                Math.Clamp(request.AnchorY, candidate.Y, candidate.Bottom)));
        }

        return placements;
    }

    private static IEnumerable<ReplayLabelBounds> NearbyCandidates(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        double clearance)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        var x = request.AnchorX;
        var y = request.AnchorY;
        var offsets = new (double X, double Y)[]
        {
            (x + clearance, y - (h / 2)),
            (x - w - clearance, y - (h / 2)),
            (x - (w / 2), y - h - clearance),
            (x - (w / 2), y + clearance),
            (x + clearance, y - h - clearance),
            (x - w - clearance, y - h - clearance),
            (x + clearance, y + clearance),
            (x - w - clearance, y + clearance),
            (x + (clearance * 2), y - (h / 2)),
            (x - w - (clearance * 2), y - (h / 2)),
            (x - (w / 2), y - h - (clearance * 2)),
            (x - (w / 2), y + (clearance * 2)),
        };

        var seen = new HashSet<(int X, int Y)>();
        foreach (var origin in offsets)
        {
            var candidate = Clamp(bounds, origin.X, origin.Y, w, h);
            if (seen.Add(((int)Math.Round(candidate.X), (int)Math.Round(candidate.Y))))
                yield return candidate;
        }
    }

    private static IEnumerable<ReplayLabelBounds> FallbackCandidates(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        double gap)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        var inset = Math.Max(4, gap);
        var left = bounds.X + inset;
        var right = bounds.Right - w - inset;
        var step = Math.Max(1, h + gap);
        var slots = Math.Max(1, (int)Math.Floor((bounds.Height - (inset * 2) + gap) / step));

        return Enumerable.Range(0, slots)
            .SelectMany(slot => new[]
            {
                new ReplayLabelBounds(left, bounds.Y + inset + (slot * step), w, h),
                new ReplayLabelBounds(right, bounds.Y + inset + (slot * step), w, h),
            })
            .Where(candidate => candidate.Bottom <= bounds.Bottom - inset + 0.01)
            .OrderBy(candidate => DistanceSquared(candidate, request.AnchorX, request.AnchorY));
    }

    private static ReplayLabelBounds BestEffortCandidate(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        IReadOnlyList<ReplayLabelBounds> protectedPoints,
        IReadOnlyList<ReplayLabelBounds> occupied)
    {
        return NearbyCandidates(bounds, request, 27)
            .Concat(FallbackCandidates(bounds, request, 2))
            .Select((candidate, index) => new
            {
                Candidate = candidate,
                Score = protectedPoints.Sum(point => IntersectionArea(point, candidate) * 1000) +
                        occupied.Sum(label => IntersectionArea(label, candidate) * 100) + index,
            })
            .OrderBy(value => value.Score)
            .Select(value => value.Candidate)
            .FirstOrDefault(Clamp(bounds, request.AnchorX, request.AnchorY, request.Width, request.Height));
    }

    private static bool IsFree(
        ReplayLabelBounds candidate,
        IReadOnlyList<ReplayLabelBounds> protectedPoints,
        IReadOnlyList<ReplayLabelBounds> occupied,
        double gap) =>
        !protectedPoints.Any(point => candidate.Intersects(point)) &&
        !occupied.Any(label => candidate.Intersects(label, gap));

    private static ReplayLabelBounds Clamp(
        ReplayLabelBounds bounds,
        double x,
        double y,
        double width,
        double height)
    {
        width = Math.Min(width, bounds.Width);
        height = Math.Min(height, bounds.Height);
        return new ReplayLabelBounds(
            Math.Clamp(x, bounds.X, bounds.Right - width),
            Math.Clamp(y, bounds.Y, bounds.Bottom - height),
            width,
            height);
    }

    private static double DistanceSquared(ReplayLabelBounds candidate, double x, double y)
    {
        var dx = candidate.X + (candidate.Width / 2) - x;
        var dy = candidate.Y + (candidate.Height / 2) - y;
        return (dx * dx) + (dy * dy);
    }

    private static double IntersectionArea(ReplayLabelBounds first, ReplayLabelBounds second)
    {
        if (!first.Intersects(second)) return 0;
        return (Math.Min(first.Right, second.Right) - Math.Max(first.X, second.X)) *
               (Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Y, second.Y));
    }
}
