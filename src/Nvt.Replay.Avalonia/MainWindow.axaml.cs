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



    private bool outputWorkspaceActive;
    private int themeMode;
    private const double ReplayTransportHeight = 64;


    public MainWindow()
    {
        InitializeComponent();
        PaintModeComboBox.ItemsSource = new SelectOption[]
        {
            new("Host", "Display host-reported touch points", nameof(ReplayRenderMode.HostState)),
            new("Report", "Display report-frame touch points", nameof(ReplayRenderMode.ReportedFrame)),
            new("Compare", "Overlay Host and Report points", nameof(ReplayRenderMode.Compare)),
        };
        PaintModeComboBox.SelectedIndex = 0;
        TrailModeComboBox.ItemsSource = new SelectOption[]
        {
            new("Recent", "Keep the latest selected number of points", nameof(ReplayTrailMode.Recent)),
            new("Until break", "Clear a contact trail after release", nameof(ReplayTrailMode.UntilBreak)),
            new("Persistent", "Keep all trails until they are cleared", nameof(ReplayTrailMode.Persistent)),
        };
        TrailModeComboBox.SelectedIndex = 1;
        LegendPositionComboBox.ItemsSource = new SelectOption[]
        {
            new("Auto", "Choose the least occupied corner for the full capture", nameof(ReplayLegendPosition.Auto)),
            new("Top left", "Pin the legend to the top-left corner", nameof(ReplayLegendPosition.TopLeft)),
            new("Top right", "Pin the legend to the top-right corner", nameof(ReplayLegendPosition.TopRight)),
            new("Bottom left", "Pin the legend to the bottom-left corner", nameof(ReplayLegendPosition.BottomLeft)),
            new("Bottom right", "Pin the legend to the bottom-right corner", nameof(ReplayLegendPosition.BottomRight)),
        };
        LegendPositionComboBox.SelectedIndex = 0;
        RegisterProfileComboBox.ItemsSource = new[] { new RegisterProfileChoice("Unconfirmed", null) }
            .Concat(NvtRegisterCatalog.Profiles.Select(profile => new RegisterProfileChoice(profile.IcFamily, profile.IcFamily)))
            .ToArray();
        RegisterFilterComboBox.ItemsSource = new RawRegisterFilterChoice[]
        {
            new("All records", RawRegisterFilter.All),
            new("Register only", RawRegisterFilter.Registers),
            new("Changed reads", RawRegisterFilter.ChangedReads),
            new("Writes / commands", RawRegisterFilter.WritesAndCommands),
            new("Ambiguous", RawRegisterFilter.Ambiguous),
        };
        configuringRegisterProfile = true;
        RegisterProfileComboBox.SelectedIndex = 0;
        configuringRegisterProfile = false;
        RegisterFilterComboBox.SelectedIndex = 0;
        OutputContentComboBox.ItemsSource = new SelectOption[]
        {
            new("MP4 video", "Preview the exact sampled replay video", "mp4"),
            new("Heatmap", "Inspect and export coordinate density", "heatmap"),
            new("Data package", "Export linked QA and automation files", "package"),
        };
        HeatmapModeComboBox.ItemsSource = new SelectOption[]
        {
            new("All recorded points", "Show every occupied cell in the selected frame range", "all"),
            new("Repeated hotspots ≥ N", "Only show cells whose sample count reaches the chosen minimum", "repeated"),
        };
        configuringOutputSettings = true;
        OutputContentComboBox.SelectedIndex = 0;
        HeatmapModeComboBox.SelectedIndex = 0;
        OutputClockComboBox.SelectedIndex = 1;
        OutputSpeedComboBox.SelectedIndex = 2;
        OutputFrameRateComboBox.SelectedIndex = 3;
        OutputResolutionComboBox.SelectedIndex = 0;
        SyncOutputResolutionWithPanel();
        configuringOutputSettings = false;
        ShortcutModulesItemsControl.ItemsSource = ReplayShortcutCatalog.Modules;
        SettingsPage.CloseRequested += (_, _) => CloseSettingsPage();
        SettingsPage.ThemeToggleRequested += (_, _) => ToggleTheme();
        SettingsPage.PlaybackPreferencesChanged += SettingsPage_OnPlaybackPreferencesChanged;
        ApplyPlaybackPreferences(SettingsPage.PlaybackPreferences, syncTransportControls: true);
        CompressIdleCheckBox.IsVisible = ClockModeComboBox.SelectedIndex == 0;
        ComboBoxAutoSizer.Fit(
            SourceAdapterComboBox,
            EventVersionComboBox,
            RegisterProfileComboBox,
            Desay97ProfileComboBox,
            RegisterFilterComboBox,
            PaintModeComboBox,
            TrailModeComboBox,
            TrailLengthComboBox,
            LegendPositionComboBox,
            ReviewOccurrenceComboBox,
            ClockModeComboBox,
            ReplaySpeedComboBox,
            OutputContentComboBox,
            OutputClockComboBox,
            OutputSpeedComboBox,
            OutputFrameRateComboBox,
            OutputResolutionComboBox,
            HeatmapModeComboBox);
        ApplyShortcutToolTips();
        AddHandler(
            InputElement.KeyDownEvent,
            MainWindow_OnPreviewKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        AddHandler(
            InputElement.KeyUpEvent,
            MainWindow_OnPreviewKeyUp,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PaintSurface.LegendCollapsedChanged += PaintSurface_OnLegendCollapsedChanged;
        RegisterActivitySurface.ActivitySelected += RegisterActivitySurface_OnActivitySelected;
        Opened += (_, _) =>
        {
            ApplyResponsiveRails(Bounds.Width);
            AttachComboBoxDismissHandlers();
        };
        SizeChanged += MainWindow_OnSizeChanged;
        if (Application.Current is { } application) application.ActualThemeVariantChanged += Application_OnActualThemeVariantChanged;
    }

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsiveRails(e.NewSize.Width);

    private void ApplyResponsiveRails(double width)
    {
        var compactTransport = width < 1300;
        EditActionLabel.IsVisible = width >= 1320;
        SaveActionLabel.IsVisible = width >= 1320;
        LoadReviewActionLabel.IsVisible = width >= 1320;
        OutputActionLabel.IsVisible = width >= 1240;
        SettingsActionLabel.IsVisible = width >= 1240;
        TimelineStatusText.IsVisible = !compactTransport;
        LoopRangeText.IsVisible = !compactTransport;
        TimelineControlBar.Margin = compactTransport ? new Thickness(12, 8) : new Thickness(22, 8);
        if (outputWorkspaceActive)
        {
            SetReviewRailCollapsed(true);
            return;
        }
        // The review queue is secondary evidence, so it never consumes the first
        // viewport unless the user explicitly opens it.
        SetReviewRailCollapsed(reviewRailUserPreference ?? true);
        SetInspectorRailCollapsed(inspectorRailUserPreference ?? width < 1240);
    }

    private void SetOutputWorkspaceChrome(bool active, bool showReplayTransport)
    {
        if (!showReplayTransport) TransportAutoPausePanel.IsVisible = false;
        ReplayTransportBorder.IsVisible = showReplayTransport;
        RootLayoutGrid.RowDefinitions[2].Height = showReplayTransport
            ? new GridLength(ReplayTransportHeight)
            : new GridLength(0);
        InspectorRailBorder.IsVisible = !active;
        InspectorGridSplitter.IsVisible = !active && !inspectorRailCollapsed;
        var inspectorColumn = WorkspaceShellGrid.ColumnDefinitions[3];
        if (active)
        {
            if (inspectorColumn.ActualWidth >= MinimumInspectorRailWidth)
                expandedInspectorRailWidth = Math.Clamp(inspectorColumn.ActualWidth, MinimumInspectorRailWidth, MaximumInspectorRailWidth);
            inspectorColumn.MinWidth = 0;
            inspectorColumn.MaxWidth = 0;
            inspectorColumn.Width = new GridLength(0);
        }
        else
        {
            SetInspectorRailCollapsed(inspectorRailUserPreference ?? Bounds.Width < 1240);
        }
    }

    private void Application_OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (paintWorkspace is null)
        {
            PaintSurface.InvalidateVisual();
            return;
        }

        ApplyPaintSettings(paintWorkspace.Settings with { Theme = CurrentReplayTheme() });
    }

    private void CommandPaletteButton_OnClick(object? sender, RoutedEventArgs e) => ToggleCommandPalette();

    private void CloseCommandPaletteButton_OnClick(object? sender, RoutedEventArgs e) => CloseCommandPalette();

    private void CommandPaletteOverlay_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseCommandPalette();
        e.Handled = true;
    }

    private void ToggleCommandPalette()
    {
        CommandPaletteOverlay.IsVisible = !CommandPaletteOverlay.IsVisible;
        if (CommandPaletteOverlay.IsVisible) CommandPaletteOverlay.Focus();
    }

    private void CloseCommandPalette()
    {
        CommandPaletteOverlay.IsVisible = false;
        Focus();
    }

    private void ShortcutAction_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ReplayShortcutAction action) return;
        CloseCommandPalette();
        ExecuteShortcut(action);
    }

    private void ApplyShortcutToolTips()
    {
        ToolTip.SetTip(PreviousFrameButton, ReplayShortcutCatalog.ToolTip(ReplayShortcutAction.PreviousFrame));
        ToolTip.SetTip(PlayPauseButton, ReplayShortcutCatalog.ToolTip(ReplayShortcutAction.TogglePlayback));
        ToolTip.SetTip(NextFrameButton, ReplayShortcutCatalog.ToolTip(ReplayShortcutAction.NextFrame));
        ToolTip.SetTip(ReplaySpeedComboBox,
            $"Replay speed · {ReplayShortcutCatalog.ToolTip(ReplayShortcutAction.Slower)} · {ReplayShortcutCatalog.ToolTip(ReplayShortcutAction.Faster)} · 1 resets");
        ToolTip.SetTip(AddMarkerButton, ReplayShortcutCatalog.ToolTip(ReplayShortcutAction.AddMarker));
    }

    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e)
        => ToggleTheme();

    private void ToggleTheme()
    {
        themeMode = (themeMode + 1) % 2;
        var variant = themeMode == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current is { } application) application.RequestedThemeVariant = variant;
    }


    private async void WorkspaceTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AnalysisTab is null) return;
        var outputSelected = ReferenceEquals(WorkspaceTabs.SelectedItem, AnalysisTab);
        var paintSelected = ReferenceEquals(WorkspaceTabs.SelectedItem, PaintTab);
        var wasOutputSelected = outputWorkspaceActive;
        outputWorkspaceActive = outputSelected;
        if (outputSelected)
        {
            StopPlayback();
            SetSourceTransportEnabled(false);
            SetOutputWorkspaceChrome(true, showReplayTransport: false);
            SetReviewRailCollapsed(true);
            if (!wasOutputSelected)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await RefreshOutputPreviewAsync();
            }
        }
        else
        {
            if (wasOutputSelected)
            {
                ExitOutputFullscreen();
                StopOutputVideoPreviewPlayback();
                outputPreviewCancellation?.Cancel();
                if (outputPreviewPlanPending) outputPlanService.Invalidate();
                outputReportService.CancelActive();
            }
            SetOutputWorkspaceChrome(false, paintSelected && !SettingsPage.IsVisible);
            SetSourceTransportEnabled(paintSelected && !SettingsPage.IsVisible);
            if (paintSelected && replaySession is { Count: > 0 })
                TimelineStatusText.Text = "Paused · Space play/pause · ←/→ step · drag Loop handles to set range";
            ApplyResponsiveRails(Bounds.Width);
        }
    }


    private void SetBusy(bool busy, string status)
    {
        operationInProgress = busy;
        LoadingProgress.IsVisible = busy;
        LoadButton.IsVisible = !busy;
        LoadButton.IsEnabled = !busy && !outputExportJobs.IsActive;
        CancelButton.IsVisible = busy;
        EventVersionComboBox.IsEnabled = !busy && session is not null;
        EditConfigurationButton.IsEnabled = !busy && session is not null;
        Desay97ProfileComboBox.IsEnabled = !busy && session is not null;
        RegisterProfileComboBox.IsEnabled = !busy && session is not null;
        ExportReadableLogButton.IsEnabled = !busy && session is not null;
        SaveReviewButton.IsEnabled = !busy && decodeConfiguration is not null;
        LoadReviewButton.IsEnabled = !busy && decodeConfiguration is not null;
        ExportAnalysisButton.IsEnabled = !busy && replaySession is { Count: > 0 };
        UpdateOutputExportButtonAvailability();
        OutputPreviewPlayPauseButton.IsEnabled = !busy && outputVideoFrameCount > 0;
        SessionStatusText.Text = status;
    }


    private void SetSourceTransportEnabled(bool enabled)
    {
        var available = enabled && replaySession is { Count: > 0 };
        PreviousFrameButton.IsEnabled = available;
        PlayPauseButton.IsEnabled = available;
        NextFrameButton.IsEnabled = available;
    }


    private static string FormatSha256(string value) => value.Length == 64
        ? $"{value[..32]}\n{value[32..]}"
        : value;

    private void MainWindow_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        HandleWindowKeyDown(e);
    }

    private void MainWindow_OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (ReplayShortcutCatalog.Match(e.Key, e.KeyModifiers) is { } shortcut &&
            (!IsShortcutEditingContext(e.Source) || shortcut.AllowWhileEditing))
        {
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled)
            HandleWindowKeyDown(e);
        base.OnKeyDown(e);
    }

    private void HandleWindowKeyDown(KeyEventArgs e)
    {
        if (outputFullscreenActive && e.Key == Key.Escape)
        {
            ExitOutputFullscreen();
            e.Handled = true;
        }
        else if (SettingsPage.IsVisible)
        {
            if (e.Key == Key.Escape) CloseSettingsPage();
            e.Handled = true;
        }
        else if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            ToggleCommandPalette();
            e.Handled = true;
        }
        else if (CommandPaletteOverlay.IsVisible)
        {
            if (e.Key == Key.Escape) CloseCommandPalette();
            e.Handled = true;
        }
        else if (ReplayShortcutCatalog.Match(e.Key, e.KeyModifiers) is { } shortcut &&
                 (!IsShortcutEditingContext(e.Source) || shortcut.AllowWhileEditing) &&
                 ExecuteShortcut(shortcut.Action))
        {
            e.Handled = true;
        }
    }

    private bool ExecuteShortcut(ReplayShortcutAction action)
    {
        switch (action)
        {
            case ReplayShortcutAction.LoadCapture:
                LoadButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.SaveReview when SaveReviewButton.IsEnabled:
                SaveReviewButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.ExportAnalysis when ExportAnalysisButton.IsEnabled:
                OpenOutputPreviewButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.TogglePlayback when outputWorkspaceActive && OutputPreviewPlayPauseButton.IsEnabled:
                OutputPreviewPlayPauseButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.TogglePlayback when PlayPauseButton.IsEnabled:
                PlayPauseButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.PreviousFrame when outputWorkspaceActive && outputVideoFrameCount > 0:
                StopOutputVideoPreviewPlayback();
                ShowOutputVideoFrame(outputVideoFrameIndex - 1);
                return true;
            case ReplayShortcutAction.PreviousFrame when PreviousFrameButton.IsEnabled:
                PreviousFrameButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.NextFrame when outputWorkspaceActive && outputVideoFrameCount > 0:
                StopOutputVideoPreviewPlayback();
                ShowOutputVideoFrame(outputVideoFrameIndex + 1);
                return true;
            case ReplayShortcutAction.NextFrame when NextFrameButton.IsEnabled:
                NextFrameButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.JumpBackTenFrames when outputWorkspaceActive && outputVideoFrameCount > 0:
                StopOutputVideoPreviewPlayback();
                ShowOutputVideoFrame(outputVideoFrameIndex - 10);
                return true;
            case ReplayShortcutAction.JumpBackTenFrames when replaySession is { Count: > 0 }:
                JumpFrames(-10);
                return true;
            case ReplayShortcutAction.JumpForwardTenFrames when outputWorkspaceActive && outputVideoFrameCount > 0:
                StopOutputVideoPreviewPlayback();
                ShowOutputVideoFrame(outputVideoFrameIndex + 10);
                return true;
            case ReplayShortcutAction.JumpForwardTenFrames when replaySession is { Count: > 0 }:
                JumpFrames(10);
                return true;
            case ReplayShortcutAction.FirstFrame when outputWorkspaceActive && outputVideoFrameCount > 0:
                StopOutputVideoPreviewPlayback();
                ShowOutputVideoFrame(0);
                return true;
            case ReplayShortcutAction.FirstFrame when replaySession is { Count: > 0 }:
                StopPlayback();
                SeekReplay(0);
                return true;
            case ReplayShortcutAction.LastFrame when outputWorkspaceActive && outputVideoFrameCount > 0:
                StopOutputVideoPreviewPlayback();
                ShowOutputVideoFrame(outputVideoFrameCount - 1);
                return true;
            case ReplayShortcutAction.LastFrame when replaySession is { Count: > 0 }:
                StopPlayback();
                SeekReplay(replaySession.Count - 1);
                return true;
            case ReplayShortcutAction.Slower:
                SelectRelativeReplaySpeed(-1);
                return true;
            case ReplayShortcutAction.Faster:
                SelectRelativeReplaySpeed(1);
                return true;
            case ReplayShortcutAction.NormalSpeed:
                SelectNormalReplaySpeed();
                return true;
            case ReplayShortcutAction.SetLoopIn when currentLogicalIndex >= 0:
                LoopInButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.SetLoopOut when currentLogicalIndex >= 0:
                LoopOutButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.AddMarker when AddMarkerButton.IsEnabled:
                AddMarkerButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.ToggleLegend when PaintTab.IsEnabled:
                LegendVisibleToggleButton.IsChecked = LegendVisibleToggleButton.IsChecked != true;
                return true;
            default:
                return false;
        }
    }


    internal static bool IsShortcutEditingContext(object? source)
    {
        if (source is not Visual visual) return false;
        return visual is TextBox or ComboBox or ComboBoxItem or Slider ||
               visual.FindAncestorOfType<TextBox>() is not null ||
               visual.FindAncestorOfType<ComboBox>() is not null ||
               visual.FindAncestorOfType<ComboBoxItem>() is not null ||
               visual.FindAncestorOfType<Slider>() is not null;
    }

    protected override void OnClosed(EventArgs e)
    {
        activeCaptureLoadGeneration = 0;
        activeCaptureDecodeGeneration = 0;
        ExitOutputFullscreen();
        StopPlayback();
        StopOutputVideoPreviewPlayback();
        ResetOutputPlanning();
        outputPlanService.Dispose();
        outputReportService.Dispose();
        var activeOperationCancellation = operationCancellation;
        operationCancellation = null;
        activeOperationCancellation?.Cancel();
        activeOperationCancellation?.Dispose();
        captureDecodeController.Dispose();
        outputExportJobs.Cancel();
        if (Application.Current is { } application) application.ActualThemeVariantChanged -= Application_OnActualThemeVariantChanged;
        base.OnClosed(e);
    }



}
