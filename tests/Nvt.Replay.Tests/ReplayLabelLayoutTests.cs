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
}
