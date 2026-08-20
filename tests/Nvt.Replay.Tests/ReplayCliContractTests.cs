namespace Nvt.Replay.Tests;

public sealed class ReplayCliContractTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("120", 120)]
    [InlineData("180", 180)]
    [InlineData("240", 240)]
    public void Export_accepts_the_same_frame_rate_range_as_the_renderer(string text, int expected)
    {
        Assert.True(ReplayCli.TryParseExportFrameRate(text, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("241")]
    [InlineData("invalid")]
    public void Export_rejects_frame_rates_outside_the_renderer_contract(string text)
    {
        Assert.False(ReplayCli.TryParseExportFrameRate(text, out _));
    }

    [Fact]
    public async Task Help_advertises_the_1_to_240_FPS_contract()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await ReplayCli.RunAsync(["help"], output, error);

        Assert.Equal(0, exitCode);
        Assert.Contains("[--fps <1-240>]", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }
}
