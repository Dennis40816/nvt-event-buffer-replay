using Nvt.Replay.Analysis;

namespace Nvt.Replay.Tests;

public sealed class ReplayLoopRangePlannerTests
{
    [Fact]
    public void Current_frame_starts_a_one_second_range_when_space_remains()
    {
        var range = ReplayLoopRangePlanner.CreateDefault(
            100,
            518,
            TimeSpan.FromSeconds(1d / 120d));

        Assert.Equal(new ReplayLoopRange(100, 220), range);
    }

    [Fact]
    public void End_of_capture_shifts_the_full_default_range_back_from_the_boundary()
    {
        var range = ReplayLoopRangePlanner.CreateDefault(
            517,
            518,
            TimeSpan.FromSeconds(1d / 120d));

        Assert.Equal(new ReplayLoopRange(397, 517), range);
    }

    [Fact]
    public void Short_capture_uses_its_entire_available_range()
    {
        var range = ReplayLoopRangePlanner.CreateDefault(
            8,
            10,
            TimeSpan.FromSeconds(1d / 120d));

        Assert.Equal(new ReplayLoopRange(0, 9), range);
    }
}
