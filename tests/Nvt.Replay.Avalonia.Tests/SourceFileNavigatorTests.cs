using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class SourceFileNavigatorTests
{
    [Theory]
    [InlineData("capture.csv", true)]
    [InlineData("CAPTURE.CSV", true)]
    [InlineData("communication.txt", false)]
    [InlineData("kernel.log", false)]
    public void Csv_sources_prefer_Excel_while_text_sources_keep_the_line_editor_route(
        string path,
        bool expected)
    {
        Assert.Equal(expected, SourceFileNavigator.PrefersExcel(path));
    }
}
