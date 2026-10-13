// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>The review and inspector rails and the remembered inspector width, owned by the UI thread.</summary>
internal sealed class InspectorLayoutState
{
    /// <summary>The review queue rail. A mutable struct, so callers use the field and not a copy.</summary>
    internal RailState ReviewRail;

    /// <summary>The inspector rail. A mutable struct, so callers use the field and not a copy.</summary>
    internal RailState InspectorRail;

    /// <summary>The inspector width to restore when the rail opens again.</summary>
    internal double ExpandedInspectorWidth = 286;
}
