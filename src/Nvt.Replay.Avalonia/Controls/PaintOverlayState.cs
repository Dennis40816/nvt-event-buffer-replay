// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Avalonia;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>The legend placement and hover state and the trail visibility of a paint surface. UI thread only.</summary>
internal sealed class PaintOverlayState
{
    internal ReplayLegendPosition LegendPosition { get; set; } = ReplayLegendPosition.TopLeft;

    internal bool LegendVisible { get; set; } = true;

    internal bool LegendCollapsed { get; set; } = true;

    internal bool LegendHovered { get; set; }

    /// <summary>Where the legend was drawn last, so pointer input can hit-test it.</summary>
    internal Rect? LegendBounds { get; set; }

    internal bool TrailLinesVisible { get; set; } = true;

    internal bool TrailPointsVisible { get; set; } = true;

    /// <summary>Forgets the legend hit area and the hover, as when the legend stops being drawn.</summary>
    internal void ClearHit()
    {
        LegendBounds = null;
        LegendHovered = false;
    }
}
