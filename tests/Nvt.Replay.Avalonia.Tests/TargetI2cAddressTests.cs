// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class TargetI2cAddressTests
{
    [Theory]
    [InlineData("0", 0)]
    [InlineData("127", 127)]
    [InlineData("  42  ", 42)]
    [InlineData("0x00", 0)]
    [InlineData("0x7F", 0x7F)]
    [InlineData("0X7f", 0x7F)]
    [InlineData(" 0x01 ", 1)]
    public void TryParseAcceptsSevenBitValues(string text, int expected)
    {
        var parsed = TargetI2cAddress.TryParse(text, out var address);

        Assert.True(parsed);
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("128")]
    [InlineData("0x80")]
    [InlineData("-1")]
    [InlineData("0x")]
    [InlineData("abc")]
    [InlineData("7F")]
    [InlineData("0x-1")]
    public void TryParseRejectsOutOfRangeOrMalformedText(string? text)
    {
        var parsed = TargetI2cAddress.TryParse(text, out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData(0, "0x00")]
    [InlineData(1, "0x01")]
    [InlineData(0x7F, "0x7F")]
    public void FormatWritesTwoDigitUppercaseHex(int address, string expected)
    {
        Assert.Equal(expected, TargetI2cAddress.Format(address));
    }
}
