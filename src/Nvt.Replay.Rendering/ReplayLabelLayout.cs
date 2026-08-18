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
        double labelGap = 5,
        IReadOnlyList<ReplayLabelBounds>? obstacles = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (bounds.Width <= 0 || bounds.Height <= 0 || requests.Count == 0)
            return [];

        var protectedPoints = new ReplayLabelBounds[requests.Count];
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
            if (!TryNearby(bounds, request, clearance, protectedPoints, occupied, index, labelGap, obstacles, out var candidate) &&
                !TryFallback(bounds, request, protectedPoints, occupied, index, labelGap, obstacles, out candidate))
                candidate = BestEffortCandidate(bounds, request, protectedPoints, occupied, index, obstacles);

            // A tiny viewport can be physically incapable of fitting every
            // label. Keep the result deterministic and inside the viewport;
            // normal replay/export sizes reach the hard non-overlap paths above.
            occupied[index] = candidate;
            placements[index] = new ReplayLabelPlacement(
                request.Key,
                candidate,
                Math.Clamp(request.AnchorX, candidate.X, candidate.Right),
                Math.Clamp(request.AnchorY, candidate.Y, candidate.Bottom));
        }

        return placements;
    }

    private static bool TryNearby(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        double clearance,
        ReplayLabelBounds[] protectedPoints,
        ReplayLabelBounds[] occupied,
        int occupiedCount,
        double gap,
        IReadOnlyList<ReplayLabelBounds>? obstacles,
        out ReplayLabelBounds result)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        for (var candidateIndex = 0; candidateIndex < 12; candidateIndex++)
        {
            var candidate = NearbyCandidate(bounds, request, clearance, w, h, candidateIndex);
            if (!IsFree(candidate, protectedPoints, occupied, occupiedCount, gap, obstacles)) continue;
            result = candidate;
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
        out ReplayLabelBounds result)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        var inset = Math.Max(4, gap);
        var left = bounds.X + inset;
        var right = bounds.Right - w - inset;
        var step = Math.Max(1, h + gap);
        var slots = Math.Max(1, (int)Math.Floor((bounds.Height - (inset * 2) + gap) / step));
        var bestDistance = double.MaxValue;
        result = default;
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
                result = candidate;
            }
        }
        return result.Width > 0;
    }

    private static ReplayLabelBounds BestEffortCandidate(
        ReplayLabelBounds bounds,
        ReplayLabelRequest request,
        ReplayLabelBounds[] protectedPoints,
        ReplayLabelBounds[] occupied,
        int occupiedCount,
        IReadOnlyList<ReplayLabelBounds>? obstacles)
    {
        var w = Math.Min(request.Width, Math.Max(1, bounds.Width));
        var h = Math.Min(request.Height, Math.Max(1, bounds.Height));
        var best = Clamp(bounds, request.AnchorX, request.AnchorY, w, h);
        var bestScore = double.MaxValue;
        for (var candidateIndex = 0; candidateIndex < 12; candidateIndex++)
        {
            var candidate = NearbyCandidate(bounds, request, 27, w, h, candidateIndex);
            var score = CandidateScore(candidate, protectedPoints, occupied, occupiedCount, obstacles) + candidateIndex;
            if (score >= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        return best;
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
        int index)
    {
        var x = request.AnchorX;
        var y = request.AnchorY;
        var origin = index switch
        {
            0 => (x + clearance, y - (height / 2)),
            1 => (x - width - clearance, y - (height / 2)),
            2 => (x - (width / 2), y - height - clearance),
            3 => (x - (width / 2), y + clearance),
            4 => (x + clearance, y - height - clearance),
            5 => (x - width - clearance, y - height - clearance),
            6 => (x + clearance, y + clearance),
            7 => (x - width - clearance, y + clearance),
            8 => (x + (clearance * 2), y - (height / 2)),
            9 => (x - width - (clearance * 2), y - (height / 2)),
            10 => (x - (width / 2), y - height - (clearance * 2)),
            _ => (x - (width / 2), y + (clearance * 2)),
        };
        return Clamp(bounds, origin.Item1, origin.Item2, width, height);
    }

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
}
