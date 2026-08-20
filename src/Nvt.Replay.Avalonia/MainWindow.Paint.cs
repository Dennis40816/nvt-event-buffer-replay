using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.Views;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private static readonly ReplayExtent DefaultPaintExtent = new(2304, 1280);
    private ReplayPaintWorkspace? paintWorkspace;
    private int zoomHintRevision;
    private bool synchronizingPaintControls;
    internal ReplayPaintWorkspace? PaintWorkspace => paintWorkspace;
    private ReplayExtent CurrentPaintExtent => paintWorkspace?.Settings.PanelExtent ?? DefaultPaintExtent;

    private ReplayRenderMode CurrentPaintMode =>
        paintWorkspace?.Settings.PointView ?? ReplayRenderMode.HostState;

    private static ReplayRenderTheme CurrentReplayTheme() =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Light
            ? ReplayRenderTheme.Light
            : ReplayRenderTheme.Dark;

    private ReplayRenderSettings CurrentReplayRenderSettings()
    {
        if (paintWorkspace is not null) return paintWorkspace.CreateRenderSettings();
        var settings = ReplayPaintSettings.Default(DefaultPaintExtent) with { Theme = CurrentReplayTheme() };
        return new ReplayRenderSettings(
            settings.PointView,
            settings.Theme,
            settings.StrongGrid,
            settings.LegendVisible,
            settings.LegendCollapsed,
            ReplayLegendPosition.TopLeft,
            ShowTrailLines: settings.TraceVisible,
            ShowTrailPoints: settings.TrailPointsVisible);
    }

    private ReplayScene CreateReplayScene(int logicalIndex)
    {
        if (paintWorkspace is null || replayFrames is null)
            throw new InvalidOperationException("A decoded replay is required.");
        return paintWorkspace.CreateScene(replayFrames[logicalIndex]);
    }

    private void RenderReplayFrame(ITouchReplaySnapshot snapshot, bool crossfade)
    {
        if (paintWorkspace is null) return;
        PaintSurface.Show(paintWorkspace.CreateScene(snapshot), crossfade);
    }

    private ReplayPaintSettings CreateInitialPaintSettings(
        ReplayExtent extent,
        ReplayPaintSettings? preserved = null,
        int replayCount = 0)
    {
        if (preserved is not null)
        {
            return preserved with
            {
                Theme = CurrentReplayTheme(),
                TrailVisibilityStart = Math.Clamp(preserved.TrailVisibilityStart, 0, replayCount),
            };
        }

        var settings = ReplayPaintSettings.Default(extent) with { Theme = CurrentReplayTheme() };
        if (PaintModeComboBox.SelectedItem is SelectOption pointView &&
            Enum.TryParse<ReplayRenderMode>(pointView.Value, out var mode))
            settings = settings with { PointView = mode };
        if (TrailModeComboBox.SelectedItem is SelectOption trailRetention &&
            Enum.TryParse<ReplayTrailMode>(trailRetention.Value, out var retention))
            settings = settings with { TrailRetention = retention };
        if (TrailLengthComboBox.SelectedItem is ComboBoxItem lengthItem &&
            int.TryParse(lengthItem.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
            settings = settings with { TrailLength = length };
        return settings;
    }

    private ReplayPaintUpdate ApplyPaintSettings(ReplayPaintSettings settings)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return default;
        var previous = paintWorkspace.Settings;
        var update = paintWorkspace.Update(settings);
        if (!update.Changed) return update;

        if (update.RequiresSurfaceUpdate)
            ApplyPaintSurfaceSettings(previous, paintWorkspace.Settings);
        if (update.RequiresFit)
        {
            PaintSurface.Fit();
            UpdatePaintZoomText();
        }
        if (update.RequiresSceneRebuild) RenderCurrentPaintScene();
        if (update.RequiresInspectorRefresh && currentInspectorRecord is not null)
            ShowRecord(currentInspectorRecord, currentInspectorFrame, currentInspectorSnapshot);
        InvalidateCurrentOutputVideoFrame();
        return update;
    }

    private void ApplyPaintSurfaceSettings(ReplayPaintSettings? previous, ReplayPaintSettings current)
    {
        if (previous is null || previous.PointView != current.PointView)
            PaintSurface.SetMode(current.PointView);
        if (previous is null ||
            previous.TraceVisible != current.TraceVisible ||
            previous.TrailPointsVisible != current.TrailPointsVisible)
            PaintSurface.SetTrailVisibility(current.TraceVisible, current.TrailPointsVisible);
        if (previous is null || previous.StrongGrid != current.StrongGrid)
            PaintSurface.SetStrongGrid(current.StrongGrid);
        if (previous is null || previous.LegendVisible != current.LegendVisible)
            PaintSurface.SetLegendVisible(current.LegendVisible);
        if (previous is null || previous.LegendCollapsed != current.LegendCollapsed)
            PaintSurface.SetLegendCollapsed(current.LegendCollapsed);
        PaintSurface.SetLegendPosition(paintWorkspace?.ResolvedLegendPosition ?? ReplayLegendPosition.TopLeft);
        if (previous is null || previous.Theme != current.Theme)
            PaintSurface.InvalidateVisual();
    }

    private void SynchronizePaintPresentation(bool fit)
    {
        if (paintWorkspace is null) return;
        var settings = paintWorkspace.Settings;
        synchronizingPaintControls = true;
        try
        {
            SelectOption(PaintModeComboBox, settings.PointView.ToString());
            SelectOption(TrailModeComboBox, settings.TrailRetention.ToString());
            TrailLengthComboBox.SelectedItem = TrailLengthComboBox.Items
                .OfType<ComboBoxItem>()
                .First(item => item.Tag?.ToString() == settings.TrailLength.ToString(CultureInfo.InvariantCulture));
            TrailLengthComboBox.IsVisible = true;
            TrailLengthLabel.IsVisible = true;
            PanelWidthTextBox.Text = settings.PanelExtent.MaximumX.ToString("0", CultureInfo.InvariantCulture);
            PanelHeightTextBox.Text = settings.PanelExtent.MaximumY.ToString("0", CultureInfo.InvariantCulture);
            TraceToggleButton.IsChecked = settings.TraceVisible;
            TrailPointsToggleButton.IsChecked = settings.TrailPointsVisible;
            GridStrengthToggleButton.IsChecked = settings.StrongGrid;
            GridStrengthToggleButton.Content = settings.StrongGrid ? "Grid +" : "Grid";
            ReverseXToggleButton.IsChecked = settings.ReverseX;
            ReverseYToggleButton.IsChecked = settings.ReverseY;
            SwapAxesToggleButton.IsChecked = settings.SwapAxes;
            SelectOption(LegendPositionComboBox, settings.LegendPosition.ToString());
            LegendVisibleToggleButton.IsChecked = settings.LegendVisible;
            LegendCompactToggleButton.IsChecked = settings.LegendCollapsed;
            ApplyPaintSurfaceSettings(previous: null, settings);
            if (fit) PaintSurface.Fit();
        }
        finally
        {
            synchronizingPaintControls = false;
        }
        UpdatePaintZoomText();
    }

    private static void SelectOption(ComboBox comboBox, string value) =>
        comboBox.SelectedItem = comboBox.Items
            .OfType<SelectOption>()
            .First(option => option.Value == value);

    private void RenderCurrentPaintScene()
    {
        if (replayFrames is null || currentLogicalIndex < 0 || currentLogicalIndex >= replayFrames.Count) return;
        RenderReplayFrame(replayFrames[currentLogicalIndex], crossfade: false);
    }

    private void RefreshPaintAnnotations()
    {
        if (paintWorkspace is null || reviewWorkspace is null) return;
        var update = paintWorkspace.UpdateAnnotations(
            reviewWorkspace.ReviewSession.Diagnostics,
            reviewWorkspace.Markers);
        if (!update.Changed) return;
        RenderCurrentPaintScene();
        InvalidateCurrentOutputVideoFrame();
    }

    private void PaintModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        if ((sender as ComboBox)?.SelectedItem is not SelectOption option ||
            !Enum.TryParse<ReplayRenderMode>(option.Value, out var mode))
            return;
        reviewWorkspace?.SetRenderMode(mode);
        ApplyPaintSettings(paintWorkspace.Settings with { PointView = mode });
    }

    private void PanelResolutionTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyPanelResolution();
        e.Handled = true;
    }

    private void PanelResolutionTextBox_OnLostFocus(object? sender, RoutedEventArgs e) => ApplyPanelResolution();

    private void ApplyPanelResolution()
    {
        if (!double.TryParse(PanelWidthTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(PanelHeightTextBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var height) ||
            width < 1 || height < 1 || width > 1_000_000 || height > 1_000_000)
        {
            ConfigurationHintText.Text = "Panel X and Y must be numbers from 1 to 1,000,000.";
            return;
        }

        if (paintWorkspace is null) return;
        var update = ApplyPaintSettings(
            paintWorkspace.Settings with { PanelExtent = new ReplayExtent(width, height) });
        if (!update.Changed) return;
        if (SyncOutputResolutionWithPanel())
            RefreshOutputPreviewIfVisible();
        StopOutputVideoPreviewPlayback();
        SessionStatusText.Text = $"Panel coordinate space · {width:0} × {height:0} · fit to canvas";
    }

    private void PaintFitButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PaintSurface.Fit();
    }

    private void UpdatePaintZoomText() =>
        PaintZoomText.Text = PaintSurface.ZoomFactor.ToString("P0", CultureInfo.InvariantCulture);

    private async void PaintSurface_OnZoomChanged(object? sender, EventArgs e)
    {
        UpdatePaintZoomText();
        if (PaintSurface.ZoomFactor == 1)
        {
            PaintZoomHintBorder.IsVisible = false;
            return;
        }

        var revision = ++zoomHintRevision;
        PaintZoomHintBorder.IsVisible = true;
        await Task.Delay(900);
        if (revision == zoomHintRevision)
            PaintZoomHintBorder.IsVisible = false;
    }

    private void TrailModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        if ((sender as ComboBox)?.SelectedItem is not SelectOption option ||
            !Enum.TryParse<ReplayTrailMode>(option.Value, out var selectedMode))
            return;
        if (TrailLengthComboBox is not null)
            TrailLengthComboBox.IsVisible = true;
        if (TrailLengthLabel is not null)
            TrailLengthLabel.IsVisible = true;
        ApplyPaintSettings(paintWorkspace.Settings with { TrailRetention = selectedMode });
    }

    private void TrailLengthComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedLength))
            return;
        ApplyPaintSettings(paintWorkspace.Settings with { TrailLength = selectedLength });
    }

    private void TraceToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        var visible = TraceToggleButton.IsChecked == true;
        ApplyPaintSettings(paintWorkspace.Settings with { TraceVisible = visible });
        SessionStatusText.Text = visible
            ? "Touch traces visible"
            : "Touch traces hidden · source data unchanged";
    }

    private void TrailPointsToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        var visible = TrailPointsToggleButton.IsChecked == true;
        ApplyPaintSettings(paintWorkspace.Settings with { TrailPointsVisible = visible });
        SessionStatusText.Text = visible
            ? "Every reported trail point is visible"
            : "Reported trail points hidden · trajectory line unchanged";
    }

    private void CanvasSettingsToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (CanvasSettingsPanel is not null)
            CanvasSettingsPanel.IsVisible = CanvasSettingsToggleButton.IsChecked == true;
    }

    private void LegendPositionComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        if ((sender as ComboBox)?.SelectedItem is not SelectOption option ||
            !Enum.TryParse<ReplayLegendPosition>(option.Value, out var selectedPosition))
            return;
        ApplyPaintSettings(paintWorkspace.Settings with { LegendPosition = selectedPosition });
    }

    private void LegendVisibleToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        ApplyPaintSettings(
            paintWorkspace.Settings with { LegendVisible = LegendVisibleToggleButton.IsChecked == true });
    }

    private void LegendCompactToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        ApplyPaintSettings(
            paintWorkspace.Settings with { LegendCollapsed = LegendCompactToggleButton.IsChecked == true });
    }

    private void PaintSurface_OnLegendCollapsedChanged(object? sender, EventArgs e)
    {
        if (LegendCompactToggleButton is not null)
            LegendCompactToggleButton.IsChecked = PaintSurface.LegendCollapsed;
    }

    private void ReverseAxisToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        ApplyPaintSettings(
            paintWorkspace.Settings with
            {
                ReverseX = ReverseXToggleButton.IsChecked == true,
                ReverseY = ReverseYToggleButton.IsChecked == true,
                SwapAxes = SwapAxesToggleButton.IsChecked == true,
            });
    }

    private void GridStrengthToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingPaintControls || paintWorkspace is null) return;
        var strong = GridStrengthToggleButton.IsChecked == true;
        GridStrengthToggleButton.Content = strong ? "Grid +" : "Grid";
        ApplyPaintSettings(paintWorkspace.Settings with { StrongGrid = strong });
    }

}
