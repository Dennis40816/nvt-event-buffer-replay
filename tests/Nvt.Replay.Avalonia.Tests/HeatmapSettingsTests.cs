// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class HeatmapSettingsTests
{
    [Fact]
    public void DefaultKeepsTheInitialHeatmapOptions()
    {
        var settings = HeatmapSettings.Default;

        Assert.Equal(5, settings.MinimumCount);
        Assert.Equal(0.65, settings.LabelThresholdRatio);
        Assert.Equal("all", settings.Mode);
    }

    [Fact]
    public void ChangingOneValueGivesADifferentRecord()
    {
        var applied = HeatmapSettings.Default;

        var next = applied with { MinimumCount = 8 };

        Assert.NotEqual(applied, next);
        Assert.Equal(applied.Mode, next.Mode);
    }
}
