// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Avalonia.Controls;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>
/// UI flags, heatmap settings and the replaceable output services of the output page.
/// Owned by the UI thread. Tests replace the services through the window's test hooks.
/// </summary>
internal sealed class OutputWindowState
{
    internal HeatmapSettings Heatmap { get; set; } = HeatmapSettings.Default;
    internal WindowState? FullscreenRestoreState { get; set; }
    internal bool RangeUserDefined { get; set; }
    internal bool EncoderWarning { get; set; }
    internal ReplayOutputPlanService PlanService { get; set; } = new();
    internal OutputReportService ReportService { get; set; } = new();
}
