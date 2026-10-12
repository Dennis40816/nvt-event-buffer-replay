// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>The heatmap options last applied to the analysis preview.</summary>
internal sealed record HeatmapSettings(int MinimumCount, double LabelThresholdRatio, string Mode)
{
    internal static HeatmapSettings Default { get; } = new(5, 0.65, "all");
}
