using Nvt.Replay.Core;
using Nvt.Replay.Formats;

namespace Nvt.Replay.Tests;

public sealed class BuiltInFormatsTests
{
    [Fact]
    public void Registry_exposes_common_versions_and_desay_97()
    {
        Assert.Equal(
            ["common-82", "common-83", "common-84", "common-85", "desay-97"],
            BuiltInFormats.All.Select(format => format.Id));
    }

    [Fact]
    public void Desay_97_requires_explicit_benz_palm_choice()
    {
        var format = Assert.Single(BuiltInFormats.All, candidate => candidate.Id == "desay-97");
        var benzPalm = Assert.Single(format.Configuration, field => field.Key == "benz-palm");

        Assert.True(benzPalm.Required);
        Assert.Equal(["Standard", "Benz"], benzPalm.Choices);
        Assert.Equal(EvidenceStatus.Provisional, format.EvidenceStatus);
    }
}
