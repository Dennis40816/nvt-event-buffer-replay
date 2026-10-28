using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class A5RenderingCharacterizationTests
{
    [Theory]
    [Trait("Contract", "docs/ui-accessibility.md: Paint rejects point/label intersections")]
    [InlineData(85, 135)]
    [InlineData(965, 135)]
    [InlineData(85, 715)]
    [InlineData(965, 715)]
    public void Corner_contact_labels_fit_an_offset_viewport_without_covering_the_point(double x, double y)
    {
        var bounds = new ReplayLabelBounds(75, 125, 900, 600);
        var request = new ReplayLabelRequest(1, x, y, 126, 42);

        // Each corner leaves ample space inward for a full-size label.
        var placement = Assert.Single(ReplayLabelLayout.Place(bounds, [request]));

        Assert.InRange(placement.Bounds.X, bounds.X, bounds.Right - request.Width);
        Assert.InRange(placement.Bounds.Y, bounds.Y, bounds.Bottom - request.Height);
        Assert.Equal(request.Width, placement.Bounds.Width);
        Assert.Equal(request.Height, placement.Bounds.Height);
        Assert.False(placement.Bounds.Intersects(new ReplayLabelBounds(x - 23, y - 23, 46, 46)));
    }

    [Fact]
    [Trait("Contract", "docs/ui-accessibility.md: Paint rejects point/label intersections and uses non-overlapping slots")]
    public void Mixed_size_clustered_labels_keep_their_sizes_and_avoid_each_other_and_all_points()
    {
        var bounds = new ReplayLabelBounds(40, 80, 1200, 800);
        var requests = Enumerable.Range(1, 10)
            .Select(id => new ReplayLabelRequest(id, 640 + (id % 2) * 3, 480 + (id % 3) * 3,
                92 + (id % 4) * 14, 32 + (id % 3) * 6))
            .ToArray();

        // Both edges have more than ten slots at the largest label height.
        var placements = ReplayLabelLayout.Place(bounds, requests);

        Assert.Equal(requests.Select(request => request.Key), placements.Select(placement => placement.Key));
        for (var index = 0; index < requests.Length; index++)
        {
            var placement = placements[index];
            Assert.Equal(requests[index].Width, placement.Bounds.Width);
            Assert.Equal(requests[index].Height, placement.Bounds.Height);
            Assert.InRange(placement.Bounds.X, bounds.X, bounds.Right - placement.Bounds.Width);
            Assert.InRange(placement.Bounds.Y, bounds.Y, bounds.Bottom - placement.Bounds.Height);
            foreach (var request in requests)
                Assert.False(placement.Bounds.Intersects(
                    new ReplayLabelBounds(request.AnchorX - 23, request.AnchorY - 23, 46, 46)));
            for (var other = index + 1; other < placements.Count; other++)
                Assert.False(placement.Bounds.Intersects(placements[other].Bounds, 5));
        }
    }

    [Theory]
    [Trait("Contract", "docs/ui-accessibility.md: Paint falls back to non-overlapping edge slots with leader lines")]
    [InlineData(true)]
    [InlineData(false)]
    public void Blocked_nearby_anchors_use_the_clear_edge_and_keep_leaders_on_label_boundaries(bool blockLeft)
    {
        var bounds = new ReplayLabelBounds(100, 50, 900, 600);
        var requests = new[]
        {
            new ReplayLabelRequest(1, 550, 350, 126, 42),
            new ReplayLabelRequest(2, 554, 354, 126, 42),
        };
        var obstacles = new[]
        {
            new ReplayLabelBounds(300, 200, 500, 300),
            new ReplayLabelBounds(blockLeft ? 100 : 840, 50, 160, 600),
        };

        // The central trail blocks every nearby anchor; the opposite 200px edge corridor is clear.
        var placements = ReplayLabelLayout.Place(bounds, requests, obstacles: obstacles);

        Assert.Equal(2, placements.Count);
        Assert.False(placements[0].Bounds.Intersects(placements[1].Bounds, 5));
        for (var index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            Assert.InRange(placement.Bounds.X, bounds.X, bounds.Right - placement.Bounds.Width);
            Assert.InRange(placement.Bounds.Y, bounds.Y, bounds.Bottom - placement.Bounds.Height);
            Assert.DoesNotContain(obstacles, obstacle => placement.Bounds.Intersects(obstacle, 1));
            foreach (var request in requests)
                Assert.False(placement.Bounds.Intersects(
                    new ReplayLabelBounds(request.AnchorX - 23, request.AnchorY - 23, 46, 46)));
            Assert.InRange(placement.LeaderY, placement.Bounds.Y, placement.Bounds.Bottom);
            if (blockLeft)
            {
                Assert.True(placement.Bounds.X > obstacles[0].Right);
                Assert.Equal(placement.Bounds.X, placement.LeaderX);
                Assert.True(placement.LeaderX > requests[index].AnchorX);
            }
            else
            {
                Assert.True(placement.Bounds.Right < obstacles[0].X);
                Assert.Equal(placement.Bounds.Right, placement.LeaderX);
                Assert.True(placement.LeaderX < requests[index].AnchorX);
            }
        }
    }

    [Fact]
    [Trait("Contract", "docs/ui-accessibility.md: Paint rejects point/label intersections and uses non-overlapping slots")]
    public void Larger_point_envelopes_and_label_gaps_are_respected_when_clear_slots_exist()
    {
        var bounds = new ReplayLabelBounds(0, 0, 1100, 720);
        var requests = new[]
        {
            new ReplayLabelRequest(1, 500, 330, 118, 42),
            new ReplayLabelRequest(2, 508, 338, 118, 42),
            new ReplayLabelRequest(3, 516, 334, 118, 42),
        };
        const double pointRadius = 52;
        const double labelGap = 12;

        // The enlarged central envelopes still leave both edge columns available.
        var placements = ReplayLabelLayout.Place(bounds, requests,
            pointRadius: pointRadius, clearance: 80, labelGap: labelGap);

        Assert.Equal(3, placements.Count);
        for (var index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            Assert.InRange(placement.Bounds.X, bounds.X, bounds.Right - placement.Bounds.Width);
            Assert.InRange(placement.Bounds.Y, bounds.Y, bounds.Bottom - placement.Bounds.Height);
            foreach (var request in requests)
                Assert.False(placement.Bounds.Intersects(new ReplayLabelBounds(
                    request.AnchorX - pointRadius, request.AnchorY - pointRadius,
                    pointRadius * 2, pointRadius * 2)));
            for (var other = index + 1; other < placements.Count; other++)
                Assert.False(placement.Bounds.Intersects(placements[other].Bounds, labelGap));
        }
    }

    [Fact]
    [Trait("Contract", "docs/ui-accessibility.md: Paint, MP4 Preview and exported MP4 share deterministic label layout")]
    public void Obstacles_and_preferred_anchors_produce_identical_results_after_unrelated_layout_calls()
    {
        var bounds = new ReplayLabelBounds(50, 100, 1000, 700);
        var requests = new[]
        {
            new ReplayLabelRequest(4, 550, 440, 126, 42, ReplayLabelAnchor.West),
            new ReplayLabelRequest(7, 556, 445, 118, 38, ReplayLabelAnchor.East),
            new ReplayLabelRequest(2, 548, 448, 110, 40, ReplayLabelAnchor.South),
        };
        var obstacles = new[] { new ReplayLabelBounds(350, 280, 400, 320) };
        var first = ReplayLabelLayout.Place(bounds, requests, obstacles: obstacles);

        Assert.Equal(3, first.Count);
        for (var repetition = 0; repetition < 10; repetition++)
        {
            _ = ReplayLabelLayout.Place(new ReplayLabelBounds(0, 0, 1400, 900),
                requests.Reverse().ToArray());
            Assert.Equal(first, ReplayLabelLayout.Place(bounds, requests, obstacles: obstacles));
        }
    }

    [Fact]
    [Trait("Contract", "README.md: legend scores the complete capture once to choose the least occupied corner")]
    [Trait("Contract", "docs/ui-accessibility.md: Legend Auto scores contact density across the complete capture")]
    public void Complete_capture_density_chooses_a_different_corner_from_first_and_last_frames()
    {
        var frames = new List<ReplayContact[]>();
        frames.Add([new ReplayContact(1, TouchType.Finger, TouchStatus.Enter, 900, 900, "synthetic-first")]);
        foreach (var point in new (ushort X, ushort Y)[] { (100, 100), (900, 100), (100, 900) })
            for (var index = 0; index < 12; index++)
                frames.Add([new ReplayContact(1, TouchType.Finger, TouchStatus.Move,
                    point.X, point.Y, $"synthetic-{point.X}-{point.Y}-{index}")]);
        var extent = new ReplayExtent(1000, 1000);

        var position = ReplayLegendPositioner.Choose(frames.SelectMany(frame => frame), extent);

        Assert.Equal(ReplayLegendPosition.BottomRight, position);
        Assert.Equal(ReplayLegendPosition.TopLeft, ReplayLegendPositioner.Choose(frames[0], extent));
        Assert.Equal(ReplayLegendPosition.TopRight, ReplayLegendPositioner.Choose(frames[^1], extent));
    }

    [Fact]
    [Trait("Contract", "docs/ui-accessibility.md: Legend Auto scores complete-capture density and remains stable")]
    public void Repeated_and_reordered_complete_capture_scoring_keeps_the_same_unique_least_occupied_corner()
    {
        var contacts = Enumerable.Range(0, 45).Select(index => new ReplayContact(
            (byte)((index % 3) + 1), TouchType.Finger, TouchStatus.Move,
            (ushort)(index < 10 || index >= 30 ? 100 : 900),
            (ushort)(index >= 30 ? 900 : 100), $"synthetic-{index}")).ToArray();
        var extent = new ReplayExtent(1000, 1000);
        var first = ReplayLegendPositioner.Choose(contacts, extent);

        Assert.Equal(ReplayLegendPosition.BottomRight, first);
        for (var repetition = 0; repetition < 10; repetition++)
        {
            _ = ReplayLegendPositioner.Choose(contacts.Take(1), extent);
            Assert.Equal(first, ReplayLegendPositioner.Choose(contacts, extent));
            Assert.Equal(first, ReplayLegendPositioner.Choose(contacts.Reverse(), extent));
        }
    }

    [Theory]
    [Trait("Contract", "README.md: Flip X, Flip Y and Swap XY transform the view; source coordinates remain unchanged")]
    [Trait("Contract", "docs/ui-accessibility.md: Legend Auto chooses the least occupied corner")]
    [InlineData(false, false, false, ReplayLegendPosition.BottomLeft)]
    [InlineData(true, false, false, ReplayLegendPosition.BottomRight)]
    [InlineData(false, true, false, ReplayLegendPosition.TopLeft)]
    [InlineData(true, true, false, ReplayLegendPosition.TopRight)]
    [InlineData(false, false, true, ReplayLegendPosition.TopRight)]
    [InlineData(true, false, true, ReplayLegendPosition.TopLeft)]
    [InlineData(false, true, true, ReplayLegendPosition.BottomRight)]
    [InlineData(true, true, true, ReplayLegendPosition.BottomLeft)]
    public void Non_square_capture_auto_legend_follows_view_transforms_without_changing_source_coordinates(
        bool reverseX, bool reverseY, bool swapAxes, ReplayLegendPosition expected)
    {
        var contacts = Enumerable.Range(0, 12).Select(index => new ReplayContact(
            1, TouchType.Finger, TouchStatus.Move,
            (ushort)(1800 + index), (ushort)(50 + index), $"synthetic-{index}")).ToArray();
        var sourceCoordinates = contacts.Select(contact => (contact.X, contact.Y)).ToArray();

        var position = ReplayLegendPositioner.Choose(contacts, new ReplayExtent(2000, 500),
            reverseX, reverseY, swapAxes);

        Assert.Equal(expected, position);
        Assert.Equal(sourceCoordinates, contacts.Select(contact => (contact.X, contact.Y)));
    }

    [Fact]
    [Trait("Contract", "docs/ui-accessibility.md: Legend Auto scores contact density across the complete capture")]
    public void Equivalent_normalized_capture_density_keeps_the_corner_when_panel_axes_scale_independently()
    {
        var contacts = new[]
        {
            new ReplayContact(1, TouchType.Finger, TouchStatus.Move, 200, 50, "synthetic-top-left"),
            new ReplayContact(2, TouchType.Finger, TouchStatus.Move, 1800, 50, "synthetic-top-right"),
            new ReplayContact(3, TouchType.Finger, TouchStatus.Move, 200, 450, "synthetic-bottom-left"),
        };
        var scaled = contacts.Select(contact => contact with
        {
            X = (ushort)(contact.X * 3),
            Y = (ushort)(contact.Y * 2),
        }).ToArray();

        var original = ReplayLegendPositioner.Choose(contacts, new ReplayExtent(2000, 500));
        var resized = ReplayLegendPositioner.Choose(scaled, new ReplayExtent(6000, 1000));

        Assert.Equal(ReplayLegendPosition.BottomRight, original);
        Assert.Equal(original, resized);
    }
}
