using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ReplayLegendPositionerTests
{
    [Fact]
    public void Dense_top_left_capture_chooses_the_opposite_corner()
    {
        var contacts = Enumerable.Range(0, 20)
            .Select(index => Contact((ushort)(80 + index), (ushort)(60 + index)))
            .ToArray();

        var position = ReplayLegendPositioner.Choose(contacts, new ReplayExtent(1_000, 1_000));

        Assert.Equal(ReplayLegendPosition.BottomRight, position);
    }

    [Fact]
    public void Axis_reversal_is_part_of_the_one_time_auto_choice()
    {
        var contacts = Enumerable.Range(0, 20)
            .Select(index => Contact((ushort)(80 + index), (ushort)(60 + index)))
            .ToArray();

        var position = ReplayLegendPositioner.Choose(
            contacts,
            new ReplayExtent(1_000, 1_000),
            reverseX: true);

        Assert.Equal(ReplayLegendPosition.BottomLeft, position);
    }

    [Fact]
    public void Empty_capture_uses_a_stable_top_left_fallback()
    {
        Assert.Equal(
            ReplayLegendPosition.TopLeft,
            ReplayLegendPositioner.Choose([], new ReplayExtent(1_000, 1_000)));
    }

    private static ReplayContact Contact(ushort x, ushort y) =>
        new(1, TouchType.Finger, TouchStatus.Move, x, y, "source");
}
