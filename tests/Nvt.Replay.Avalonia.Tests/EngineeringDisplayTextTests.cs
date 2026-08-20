using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class EngineeringDisplayTextTests
{
    [Fact]
    public void Diagnostic_break_opportunities_preserve_the_exact_code()
    {
        const string code = "CAPTURE_END_ACTIVE_CONTACTS";

        var display = EngineeringDisplayText.AddBreakOpportunities(code);

        Assert.Contains("_\u200B", display);
        Assert.Equal(code, EngineeringDisplayText.RemoveBreakOpportunities(display));
    }

    [Fact]
    public void Long_source_fields_put_the_value_on_a_controlled_new_line()
    {
        var display = EngineeringDisplayText.FormatKeyValue("register_profile_resolution", "resolved");

        Assert.Contains("=\n  resolved", EngineeringDisplayText.RemoveBreakOpportunities(display));
        Assert.DoesNotContain("res\nolved", display, StringComparison.Ordinal);
    }
}
