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
    double Height,
    ReplayLabelAnchor? PreferredAnchor = null);

public enum ReplayLabelAnchor
{
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    NorthWest,
}

public readonly record struct ReplayLabelPlacement(
    int Key,
    ReplayLabelBounds Bounds,
    double LeaderX,
    double LeaderY,
    ReplayLabelAnchor Anchor);

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
        double clearance = 40,
        double labelGap = 5,
        IReadOnlyList<ReplayLabelBounds>? obstacles = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (bounds.Width <= 0 || bounds.Height <= 0 || requests.Count == 0)
            return [];

        var protectedPoints = new ReplayLabelBounds[requests.Count];
        var initialAnchors = ResolveInitialAnchors(bounds, requests);
        var occupied = new ReplayLabelBounds[requests.Count];
        var placements = new ReplayLabelPlacement[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            protectedPoints[index] = new ReplayLabelBounds(
                request.AnchorX - pointRadius,
                request.AnchorY - pointRadius,
                pointRadius * 2,
                pointRadius * 2);
        }

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var preferred = request.PreferredAnchor ?? initialAnchors[index];
            if (!TryNearby(bounds, request, preferred, clearance, protectedPoints, occupied, index, labelGap, obstacles, out var candidate) &&
                !TryFallback(bounds, request, protectedPoints, occupied, index, labelGap, obstacles, out candidate))
                candidate = BestEffortCandidate(bounds, request, preferred, protectedPoints, occupied, index, obstacles);

            // A tiny viewport can be physically incapable of fitting every
            // label. Keep the result deterministic and inside the viewport;
            // normal replay/export sizes reach the hard non-overlap paths above.
            occupied[index] = candidate.Bounds;
            placements[index] = new ReplayLabelPlacement(
                request.Key,
                candidate.Bounds,
                Math.Clamp(request.AnchorX, candidate.Bounds.X, candidate.Bounds.Right),
                Math.Clamp(request.AnchorY, candidate.Bounds.Y, candidate.Bounds.Bottom),
                candidate.Anchor);
        }

        return placements;
    }

    private static bool TryNearby(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        ReplayLabelAnchor preferred,
        double clearance,
        ReplayLabelBounds[] protectedPoints,
        ReplayLabelBounds[] occupied,
        int occupiedCount,
        double gap,
        IReadOnlyList<ReplayLabelBounds>? obstacles,
        out LabelCandidate result)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        foreach (var (anchor, distanceScale) in CandidateSequence(preferred))
        {
            var candidate = NearbyCandidate(bounds, request, clearance * distanceScale, w, h, anchor);
            if (!IsFree(candidate, protectedPoints, occupied, occupiedCount, gap, obstacles)) continue;
            result = new LabelCandidate(candidate, anchor);
            return true;
        }
        result = default;
        return false;
    }

    private static bool TryFallback(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        ReplayLabelBounds[] protectedPoints,
        ReplayLabelBounds[] occupied,
        int occupiedCount,
        double gap,
        IReadOnlyList<ReplayLabelBounds>? obstacles,
        out LabelCandidate result)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        var inset = Math.Max(4, gap);
        var left = bounds.X + inset;
        var right = bounds.Right - w - inset;
        var step = Math.Max(1, h + gap);
        var slots = Math.Max(1, (int)Math.Floor((bounds.Height - (inset * 2) + gap) / step));
        var bestDistance = double.MaxValue;
        ReplayLabelBounds best = default;
        for (var slot = 0; slot < slots; slot++)
        {
            var y = bounds.Y + inset + (slot * step);
            for (var side = 0; side < 2; side++)
            {
                var candidate = new ReplayLabelBounds(side == 0 ? left : right, y, w, h);
                if (candidate.Bottom > bounds.Bottom - inset + 0.01 ||
                    !IsFree(candidate, protectedPoints, occupied, occupiedCount, gap, obstacles))
                    continue;
                var distance = DistanceSquared(candidate, request.AnchorX, request.AnchorY);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = candidate;
            }
        }
        var anchor = best.X + (best.Width / 2) < request.AnchorX
            ? ReplayLabelAnchor.West
            : ReplayLabelAnchor.East;
        result = new LabelCandidate(best, anchor);
        return best.Width > 0;
    }

    private static LabelCandidate BestEffortCandidate(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        ReplayLabelAnchor preferred,
        ReplayLabelBounds[] protectedPoints,
        ReplayLabelBounds[] occupied,
        int occupiedCount,
        IReadOnlyList<ReplayLabelBounds>? obstacles)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        var best = new LabelCandidate(Clamp(bounds, request.AnchorX, request.AnchorY, w, h), preferred);
        var bestScore = double.MaxValue;
        var candidateIndex = 0;
        foreach (var (anchor, distanceScale) in CandidateSequence(preferred))
        {
            var candidate = NearbyCandidate(bounds, request, 40 * distanceScale, w, h, anchor);
            var score = CandidateScore(candidate, protectedPoints, occupied, occupiedCount, obstacles) + candidateIndex;
            candidateIndex++;
            if (score >= bestScore) continue;
            best = new LabelCandidate(candidate, anchor);
            bestScore = score;
        }
        return best;
    }

    private static ReplayLabelAnchor[] ResolveInitialAnchors(
        ReplayLabelBounds bounds,
        IReadOnlyList<ReplayLabelRequest> requests)
    {
        var result = new ReplayLabelAnchor[requests.Count];
        var centerX = requests.Average(request => request.AnchorX);
        var centerY = requests.Average(request => request.AnchorY);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var dx = (request.AnchorX - centerX) / Math.Max(1, bounds.Width);
            var dy = (request.AnchorY - centerY) / Math.Max(1, bounds.Height);
            result[index] = Math.Abs(dx) + Math.Abs(dy) < 0.0001
                ? RoomiestAnchor(bounds, request)
                : DirectionAnchor(dx, dy);
        }
        return result;
    }

    private static ReplayLabelAnchor DirectionAnchor(double dx, double dy)
    {
        var horizontal = Math.Abs(dx);
        var vertical = Math.Abs(dy);
        if (horizontal >= vertical * 1.45)
            return dx < 0 ? ReplayLabelAnchor.West : ReplayLabelAnchor.East;
        if (vertical >= horizontal * 1.45)
            return dy < 0 ? ReplayLabelAnchor.North : ReplayLabelAnchor.South;
        if (dx < 0)
            return dy < 0 ? ReplayLabelAnchor.NorthWest : ReplayLabelAnchor.SouthWest;
        return dy < 0 ? ReplayLabelAnchor.NorthEast : ReplayLabelAnchor.SouthEast;
    }

    private static ReplayLabelAnchor RoomiestAnchor(ReplayLabelBounds bounds, ReplayLabelRequest request)
    {
        var north = request.AnchorY - bounds.Y - request.Height;
        var south = bounds.Bottom - request.AnchorY - request.Height;
        var west = request.AnchorX - bounds.X - request.Width;
        var east = bounds.Right - request.AnchorX - request.Width;
        var rooms = new[]
        {
            (Anchor: ReplayLabelAnchor.North, Room: north),
            (Anchor: ReplayLabelAnchor.South, Room: south),
            (Anchor: ReplayLabelAnchor.West, Room: west),
            (Anchor: ReplayLabelAnchor.East, Room: east),
        };
        return rooms.MaxBy(item => item.Room).Anchor;
    }

    private static bool IsFree(
        ReplayLabelBounds candidate,
        ReplayLabelBounds[] protectedPoints,
        ReplayLabelBounds[] occupied,
        int occupiedCount,
        double gap,
        IReadOnlyList<ReplayLabelBounds>? obstacles)
    {
        if (obstacles is not null)
            for (var index = 0; index < obstacles.Count; index++)
                if (candidate.Intersects(obstacles[index], 1)) return false;
        for (var index = 0; index < protectedPoints.Length; index++)
            if (candidate.Intersects(protectedPoints[index])) return false;
        for (var index = 0; index < occupiedCount; index++)
            if (candidate.Intersects(occupied[index], gap)) return false;
        return true;
    }

    private static ReplayLabelBounds NearbyCandidate(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        double clearance,
        double width,
        double height,
        ReplayLabelAnchor anchor)
    {
        var x = request.AnchorX;
        var y = request.AnchorY;
        var origin = anchor switch
        {
            ReplayLabelAnchor.North => (x - (width / 2), y - height - clearance),
            ReplayLabelAnchor.NorthEast => (x + clearance, y - height - clearance),
            ReplayLabelAnchor.East => (x + clearance, y - (height / 2)),
            ReplayLabelAnchor.SouthEast => (x + clearance, y + clearance),
            ReplayLabelAnchor.South => (x - (width / 2), y + clearance),
            ReplayLabelAnchor.SouthWest => (x - width - clearance, y + clearance),
            ReplayLabelAnchor.West => (x - width - clearance, y - (height / 2)),
            _ => (x - width - clearance, y - height - clearance),
        };
        return Clamp(bounds, origin.Item1, origin.Item2, width, height);
    }

    private static IEnumerable<(ReplayLabelAnchor Anchor, double DistanceScale)> CandidateSequence(
        ReplayLabelAnchor preferred)
    {
        yield return (preferred, 1);
        yield return (Rotate(preferred, 1), 1);
        yield return (Rotate(preferred, -1), 1);
        yield return (preferred, 1.75);
        yield return (Rotate(preferred, 2), 1);
        yield return (Rotate(preferred, -2), 1);
        yield return (Rotate(preferred, 1), 1.75);
        yield return (Rotate(preferred, -1), 1.75);
        yield return (Rotate(preferred, 3), 1);
        yield return (Rotate(preferred, -3), 1);
        yield return (Rotate(preferred, 4), 1);
        yield return (Rotate(preferred, 2), 1.75);
        yield return (Rotate(preferred, -2), 1.75);
        yield return (Rotate(preferred, 3), 1.75);
        yield return (Rotate(preferred, -3), 1.75);
        yield return (Rotate(preferred, 4), 1.75);
    }

    private static ReplayLabelAnchor Rotate(ReplayLabelAnchor anchor, int steps) =>
        (ReplayLabelAnchor)(((int)anchor + steps + 8) % 8);

    private static double CandidateScore(
        ReplayLabelBounds candidate,
        ReplayLabelBounds[] protectedPoints,
        ReplayLabelBounds[] occupied,
        int occupiedCount,
        IReadOnlyList<ReplayLabelBounds>? obstacles)
    {
        double score = 0;
        if (obstacles is not null)
            for (var index = 0; index < obstacles.Count; index++)
                score += IntersectionArea(obstacles[index], candidate) * 25;
        for (var index = 0; index < protectedPoints.Length; index++)
            score += IntersectionArea(protectedPoints[index], candidate) * 1000;
        for (var index = 0; index < occupiedCount; index++)
            score += IntersectionArea(occupied[index], candidate) * 100;
        return score;
    }

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

    private readonly record struct LabelCandidate(ReplayLabelBounds Bounds, ReplayLabelAnchor Anchor);
}
