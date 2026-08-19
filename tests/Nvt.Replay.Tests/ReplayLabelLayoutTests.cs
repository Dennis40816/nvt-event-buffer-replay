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

    [Fact]
    public void Lower_outlier_uses_a_distant_south_anchor_from_contact_geometry()
    {
        var request = new ReplayLabelRequest(99, 450, 410, 126, 42);

        var placement = ReplayLabelLayout.Place(
            new ReplayLabelBounds(0, 0, 900, 600),
            [
                new ReplayLabelRequest(7, 420, 220, 126, 42),
                new ReplayLabelRequest(3, 480, 220, 126, 42),
                request,
            ]).Single(item => item.Key == request.Key);

        Assert.Equal(ReplayLabelAnchor.South, placement.Anchor);
        Assert.True(placement.Bounds.Y >= request.AnchorY + 40);
        Assert.True(placement.LeaderY >= request.AnchorY + 40);
    }

    [Fact]
    public void Contact_identifier_does_not_choose_the_initial_anchor()
    {
        var bounds = new ReplayLabelBounds(0, 0, 900, 600);
        var first = ReplayLabelLayout.Place(bounds,
        [
            new ReplayLabelRequest(1, 250, 300, 126, 42),
            new ReplayLabelRequest(4, 650, 300, 126, 42),
        ]);
        var swapped = ReplayLabelLayout.Place(bounds,
        [
            new ReplayLabelRequest(4, 250, 300, 126, 42),
            new ReplayLabelRequest(1, 650, 300, 126, 42),
        ]);

        Assert.Equal(first[0].Anchor, swapped[0].Anchor);
        Assert.Equal(first[1].Anchor, swapped[1].Anchor);
        Assert.Equal(ReplayLabelAnchor.West, first[0].Anchor);
        Assert.Equal(ReplayLabelAnchor.East, first[1].Anchor);
    }

    [Fact]
    public void Previous_anchor_is_retained_during_small_contact_motion()
    {
        var bounds = new ReplayLabelBounds(0, 0, 900, 600);
        var first = Assert.Single(ReplayLabelLayout.Place(
            bounds,
            [new ReplayLabelRequest(4, 450, 260, 126, 42)]));
        var moved = Assert.Single(ReplayLabelLayout.Place(
            bounds,
            [new ReplayLabelRequest(4, 453, 262, 126, 42, first.Anchor)]));

        Assert.Equal(first.Anchor, moved.Anchor);
        Assert.Equal(first.Bounds.X + 3, moved.Bounds.X, precision: 3);
        Assert.Equal(first.Bounds.Y + 2, moved.Bounds.Y, precision: 3);
    }

    [Fact]
    public void Preferred_anchor_relocates_only_when_it_would_cross_the_viewport()
    {
        var bounds = new ReplayLabelBounds(0, 0, 640, 400);

        var placement = Assert.Single(ReplayLabelLayout.Place(
            bounds,
            [new ReplayLabelRequest(4, 320, 382, 126, 42, ReplayLabelAnchor.South)]));

        Assert.NotEqual(ReplayLabelAnchor.South, placement.Anchor);
        Assert.True(placement.Bounds.Bottom <= bounds.Bottom);
        Assert.False(placement.Bounds.Intersects(new ReplayLabelBounds(297, 359, 46, 46)));
    }
}
