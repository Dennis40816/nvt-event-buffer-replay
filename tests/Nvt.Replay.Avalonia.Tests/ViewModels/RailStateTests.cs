// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests.ViewModels;

public sealed class RailStateTests
{
    [Fact]
    public void DefaultStateStartsExpandedWithoutUserPreference()
    {
        var state = default(RailState);

        Assert.False(state.Collapsed);
        Assert.Null(state.UserPreference);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UserToggleRecordsOppositeOfCollapsedState(bool collapsed, bool? previousPreference)
    {
        var state = new RailState(collapsed, previousPreference);

        var requestedCollapse = state.ToggleUserPreference();

        Assert.Equal(!collapsed, requestedCollapse);
        Assert.Equal(!collapsed, state.UserPreference);
        Assert.Equal(collapsed, state.Collapsed);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, null)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AutomaticCollapsePreservesUserPreference(bool collapsed, bool? userPreference)
    {
        var state = new RailState(!collapsed, userPreference);

        state.SetCollapsed(collapsed);

        Assert.Equal(collapsed, state.Collapsed);
        Assert.Equal(userPreference, state.UserPreference);
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(null, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void DefaultResolutionUsesPreferenceBeforeFallback(bool? userPreference, bool fallback, bool expected)
    {
        var state = new RailState(!fallback, userPreference);

        var collapsed = state.ResolveDefault(fallback);

        Assert.Equal(expected, collapsed);
    }
}
