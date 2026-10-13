// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class RegisterProfileDecodeGateTests
{
    [Theory]
    [InlineData(-1, null, -1, false)]
    [InlineData(-1, "51927", 0, false)]
    [InlineData(0, null, -1, true)]
    [InlineData(0, "51927", 0, true)]
    [InlineData(3, null, -1, true)]
    [InlineData(4, null, -1, false)]
    [InlineData(4, null, 0, false)]
    [InlineData(4, "51927", -1, false)]
    [InlineData(4, "51927", 0, true)]
    [InlineData(4, "51927", 2, true)]
    [InlineData(5, null, -1, true)]
    public void CanDecodeFollowsTheVersionAndDesay97Rules(
        int versionIndex, string? icFamily, int palmProfileIndex, bool expected)
    {
        Assert.Equal(expected, RegisterProfileDecodeGate.CanDecode(versionIndex, icFamily, palmProfileIndex));
    }

    [Fact]
    public void Desay97VersionIndexPointsAtTheFifthVersionItem()
    {
        Assert.Equal(4, RegisterProfileDecodeGate.Desay97VersionIndex);
    }
}
