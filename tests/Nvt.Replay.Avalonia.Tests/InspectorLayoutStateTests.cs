// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class InspectorLayoutStateTests
{
    [Fact]
    public void StartsWithExpandedRailsAndTheDefaultInspectorWidth()
    {
        var layout = new InspectorLayoutState();

        Assert.False(layout.ReviewRail.Collapsed);
        Assert.False(layout.InspectorRail.Collapsed);
        Assert.Null(layout.ReviewRail.UserPreference);
        Assert.Equal(286, layout.ExpandedInspectorWidth);
    }

    [Fact]
    public void RailsKeepTheirOwnStateAcrossMutatingCalls()
    {
        var layout = new InspectorLayoutState();

        var collapsed = layout.InspectorRail.ToggleUserPreference();
        layout.InspectorRail.SetCollapsed(collapsed);

        Assert.True(layout.InspectorRail.Collapsed);
        Assert.True(layout.InspectorRail.UserPreference);
        Assert.False(layout.ReviewRail.Collapsed);
        Assert.Null(layout.ReviewRail.UserPreference);
    }
}
