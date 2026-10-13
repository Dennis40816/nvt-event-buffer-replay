// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class RegisterProfileChoicesTests
{
    private static RegisterProfileChoice[] Choices() =>
        [new("Unconfirmed", null), new("51927", "51927"), new("51950/51951", "51950/51951")];

    [Fact]
    public void FindMatchesTheIcFamily()
    {
        var choices = Choices();

        Assert.Same(choices[1], RegisterProfileChoices.Find(choices, "51927"));
        Assert.Same(choices[2], RegisterProfileChoices.Find(choices, "51950/51951"));
    }

    [Fact]
    public void FindIgnoresCaseOfTheFamilyText()
    {
        RegisterProfileChoice[] choices = [new("x", "ic-x")];

        Assert.Same(choices[0], RegisterProfileChoices.Find(choices, "IC-X"));
    }

    [Fact]
    public void FindReturnsTheUnconfirmedChoiceForNullFamily()
    {
        var choices = Choices();

        Assert.Same(choices[0], RegisterProfileChoices.Find(choices, null));
    }

    [Fact]
    public void FindReturnsNullWhenNothingMatches()
    {
        var choices = Choices();
        RegisterProfileChoice[] withoutUnconfirmed = [choices[1]];

        Assert.Null(RegisterProfileChoices.Find(choices, "99999"));
        Assert.Null(RegisterProfileChoices.Find(withoutUnconfirmed, null));
    }

    [Fact]
    public void FindReturnsNullForASourceThatIsNotAChoiceList()
    {
        string[] notChoices = ["51927"];

        Assert.Null(RegisterProfileChoices.Find(null, "51927"));
        Assert.Null(RegisterProfileChoices.Find(notChoices, "51927"));
    }
}
