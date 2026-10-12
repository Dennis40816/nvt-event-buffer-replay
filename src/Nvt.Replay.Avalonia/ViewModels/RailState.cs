// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Rail visibility and the user's explicit collapse preference, owned by the UI thread.</summary>
internal struct RailState
{
    internal RailState(bool collapsed, bool? userPreference = null)
    {
        Collapsed = collapsed;
        UserPreference = userPreference;
    }

    internal bool Collapsed { get; private set; }
    internal bool? UserPreference { get; private set; }

    // Record the preference before MainWindow applies visibility through its existing setter.
    internal bool ToggleUserPreference()
    {
        var collapsed = !Collapsed;
        UserPreference = collapsed;
        return collapsed;
    }

    internal void SetCollapsed(bool collapsed) => Collapsed = collapsed;

    internal readonly bool ResolveDefault(bool fallback) => UserPreference ?? fallback;
}
