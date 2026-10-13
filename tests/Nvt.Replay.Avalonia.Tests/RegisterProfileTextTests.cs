// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class RegisterProfileTextTests
{
    [Fact]
    public void BusyNamesTheProfileOrTheRemoval()
    {
        Assert.Equal("Removing IC register profile", RegisterProfileText.Busy(null));
        Assert.Equal("Applying IC profile 51927", RegisterProfileText.Busy("51927"));
    }

    [Fact]
    public void AppliedHintReportsTheProfileOrTheRawOnlyCount()
    {
        Assert.Equal(
            "IC profile 51927 confirmed · register semantics and Event Buffer base reapplied",
            RegisterProfileText.AppliedHint("51927", 3));
        Assert.Equal(
            "IC profile unconfirmed · 3 collision-prone register events remain raw-only",
            RegisterProfileText.AppliedHint(null, 3));
    }

    [Fact]
    public void AppliedStatusStatesThatSourceBytesAreUnchanged()
    {
        Assert.Equal(
            "Register profile 51927 applied without changing source bytes",
            RegisterProfileText.AppliedStatus("51927"));
        Assert.Equal(
            "Register profile removed; original capture remains unchanged",
            RegisterProfileText.AppliedStatus(null));
    }

    [Fact]
    public void NoAddressEvidenceHintFormatsTheAddressAsTwoHexDigits()
    {
        Assert.Equal(
            "I²C 0x0A selected · no complete Event Buffer address evidence; IC profile remains unconfirmed.",
            RegisterProfileText.NoAddressEvidenceHint(0x0A));
    }
}
