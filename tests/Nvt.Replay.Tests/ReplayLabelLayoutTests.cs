using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ReplayLabelLayoutTests
{
    [Fact]
    public void Clustered_ten_contact_labels_use_non_overlapping_slots()
    {
        var bounds = new ReplayLabelBounds(0, 0, 900, 600);
        var requests = Enumerable.Range(1, 10)
            .Select(id => new ReplayLabelRequest(id, 450 + (id % 2), 300 + (id % 3), 126, 42))
            .ToArray();

        var placements = ReplayLabelLayout.Place(bounds, requests);

        Assert.Equal(10, placements.Count);
        foreach (var placement in placements)
        {
            Assert.True(placement.Bounds.X >= bounds.X);
            Assert.True(placement.Bounds.Y >= bounds.Y);
            Assert.True(placement.Bounds.Right <= bounds.Right);
            Assert.True(placement.Bounds.Bottom <= bounds.Bottom);
        }

        for (var left = 0; left < placements.Count; left++)
        for (var right = left + 1; right < placements.Count; right++)
            Assert.False(placements[left].Bounds.Intersects(placements[right].Bounds));
    }

    [Fact]
    public void Label_layout_is_deterministic_and_avoids_all_contact_points()
    {
        var bounds = new ReplayLabelBounds(0, 0, 640, 400);
        var requests = Enumerable.Range(1, 6)
            .Select(id => new ReplayLabelRequest(id, 300 + id, 190 + id, 118, 38))
            .ToArray();

        var first = ReplayLabelLayout.Place(bounds, requests);
        var second = ReplayLabelLayout.Place(bounds, requests);

        Assert.Equal(first, second);
        foreach (var placement in first)
        foreach (var request in requests)
        {
            var protectedPoint = new ReplayLabelBounds(request.AnchorX - 23, request.AnchorY - 23, 46, 46);
            Assert.False(placement.Bounds.Intersects(protectedPoint));
        }
    }

    [Fact]
    public void Dense_trail_obstacles_push_labels_into_clear_readable_slots()
    {
        var bounds = new ReplayLabelBounds(0, 0, 640, 400);
        var requests = new[]
        {
            new ReplayLabelRequest(1, 320, 200, 126, 42),
            new ReplayLabelRequest(2, 334, 208, 126, 42),
        };
        var obstacles = new List<ReplayLabelBounds>();
        for (var x = 180; x <= 480; x += 8)
        for (var y = 120; y <= 280; y += 8)
            obstacles.Add(new ReplayLabelBounds(x - 3, y - 3, 6, 6));

        var placements = ReplayLabelLayout.Place(bounds, requests, obstacles: obstacles);

        Assert.Equal(2, placements.Count);
        Assert.False(placements[0].Bounds.Intersects(placements[1].Bounds));
        Assert.All(placements, placement =>
            Assert.DoesNotContain(obstacles, obstacle => placement.Bounds.Intersects(obstacle, 1)));
        Assert.All(placements, placement =>
            Assert.True(placement.Bounds.Right <= 180 || placement.Bounds.X >= 480));
    }
}
