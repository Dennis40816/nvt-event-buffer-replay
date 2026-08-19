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
    private CancellationTokenSource? operationCancellation;
    private CaptureSession? session;
    private ITouchReplaySession? replaySession;
    private ReplayFrameCache? replayFrames;
    private RawRecordRow[] allRawRows = [];
    private RegisterActivityEntry[] registerActivities = [];
    private Dictionary<string, RawRecordRow> rawRowsById = [];
    private Dictionary<string, DecodedFrameRow> decodedRowsBySourceId = [];
    private DecodedFrameRow[] decodedRows = [];
    private DiagnosticRow[] diagnosticRows = [];
    private int[] diagnosticLineNumbers = [];
    private ReviewSession? reviewSession;
    private ReviewGroupRow[] reviewRows = [];
    private ReviewQueueFilter reviewFilter = ReviewQueueFilter.All;
    private string? selectedReviewGroupId;
    private string? selectedMarkerId;
    private IReadOnlyList<ReplayDiagnostic> baseDiagnostics = [];
    private readonly List<ReplayMarker> markers = [];
    private ReplayDecodeConfiguration? decodeConfiguration;
    private ReplaySidecarOpenResult? pendingSidecar;
    private ReplayExtent replayExtent = new(2304, 1280);
    private CancellationTokenSource? playbackCancellation;
    private CancellationTokenSource? outputPreviewCancellation;
    private CancellationTokenSource? outputVideoPlaybackCancellation;
    private CancellationTokenSource? outputExportCancellation;
    private IReadOnlyList<ReplayFramePlanEntry> outputVideoPlan = [];
    private int outputVideoFrameCount;
    private int outputVideoFrameIndex;
    private int outputVideoWidth = 2304;
    private int outputVideoHeight = 1280;
    private int outputVideoFrameRate = 30;
    private double outputVideoSpeed = 1;
    private ReplayExportClock outputVideoClock = ReplayExportClock.Frame;
    private CaptureAnalysisReport? currentOutputReport;
    private int heatmapMinimumCount = 5;
    private double heatmapLabelThresholdRatio = 0.65;
    private string appliedHeatmapMode = "all";
    private bool outputFullscreenActive;
    private WindowState outputFullscreenPreviousWindowState = WindowState.Normal;
    private int currentLogicalIndex = -1;
    private int? loopIn;
    private int? loopOut;
    private bool loopEnabled;
    private double replaySpeed = 1;
    private int trailLength = 10;
    private ReplayTrailMode trailMode = ReplayTrailMode.UntilBreak;
    private bool traceVisible = true;
    private bool trailPointsVisible = true;
    private ReplayLegendPosition legendPosition = ReplayLegendPosition.Auto;
    private ReplayTrailHistory? trailHistory;
    private ReplayAutoPauseIndex? autoPauseIndex;
    private int trailVisibilityStart;
    private int zoomHintRevision;
    private bool reverseX;
    private bool reverseY;
    private bool swapAxes;
    private bool maxReplaySpeed;
    private bool synchronizingSelection;
    private bool synchronizingLoopControls;
    private bool configuringEventVersion;
    private string? pendingSourcePath;
    private bool configuringSourceChoice;
    private bool configuringRegisterProfile;
    private bool configuringOutputSettings;
    private bool synchronizingAutoPauseControls;
    private bool operationInProgress;
    private bool outputExportInProgress;
    private int playbackTimingRevision;
    private int playbackRunGeneration;
    private bool outputWorkspaceActive;
    private bool comboBoxDismissHandlersAttached;
    private int themeMode;
    private bool reviewRailCollapsed;
    private bool inspectorRailCollapsed;
    private bool? reviewRailUserPreference;
    private bool? inspectorRailUserPreference;
    private SourceRecord? currentInspectorRecord;
    private object? currentInspectorFrame;
    private ITouchReplaySnapshot? currentInspectorSnapshot;
    private InspectorFramePresentation? currentInspectorPresentation;
    private double expandedInspectorRailWidth = 286;
    private const double ExpandedReviewRailWidth = 260;
    private const double DefaultInspectorRailWidth = 286;
    private const double MinimumInspectorRailWidth = 280;
    private const double MaximumInspectorRailWidth = 420;
    private const double CollapsedInspectorRailWidth = 58;
    private const double ReplayTransportHeight = 64;
    private static readonly TimeSpan MinimumPlaybackVisualInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan PlaybackDetailPresentationInterval = TimeSpan.FromMilliseconds(100);
    private long lastDetailPresentationTimestamp;
    internal int DetailPresentationCount { get; private set; }
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
            new("VIDEO SETTINGS", "Preview the exact sampled replay video", "mp4"),
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
        OutputFrameRateComboBox.SelectedIndex = 1;
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

    private void AttachComboBoxDismissHandlers()
    {
        if (comboBoxDismissHandlersAttached) return;
        comboBoxDismissHandlersAttached = true;
        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
            comboBox.DropDownClosed += ComboBox_OnAnyDropDownClosed;
    }

    private void ComboBox_OnAnyDropDownClosed(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);

    private void ApplyResponsiveRails(double width)
    {
        EditActionLabel.IsVisible = width >= 1320;
        SaveActionLabel.IsVisible = width >= 1320;
        OutputActionLabel.IsVisible = width >= 1240;
        SettingsActionLabel.IsVisible = width >= 1240;
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

    private void ReviewRailToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        reviewRailUserPreference = !reviewRailCollapsed;
        SetReviewRailCollapsed(reviewRailUserPreference.Value);
    }

    private void InspectorRailToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        inspectorRailUserPreference = !inspectorRailCollapsed;
        SetInspectorRailCollapsed(inspectorRailUserPreference.Value);
    }

    private async void CopyInspectorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentInspectorPresentation is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(InspectorPresentationBuilder.BuildCopyText(currentInspectorPresentation));
        SessionStatusText.Text = $"Copied frame {currentInspectorPresentation.LogicalLabel} summary";
    }

    private void PreviousFindingButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFinding(-1);

    private void NextFindingButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFinding(1);

    private void NavigateFinding(int direction)
    {
        if (reviewSession is null || replaySession is null) return;
        var indices = reviewSession.Groups
            .SelectMany(group => group.Occurrences)
            .Where(occurrence => occurrence.LogicalIndex is not null)
            .Select(occurrence => occurrence.LogicalIndex!.Value)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (indices.Length == 0) return;

        var target = direction < 0
            ? indices.Where(index => index < currentLogicalIndex).DefaultIfEmpty(indices[^1]).Last()
            : indices.Where(index => index > currentLogicalIndex).DefaultIfEmpty(indices[0]).First();
        StopPlayback();
        SeekReplay(target);
        reviewFilter = ReviewQueueFilter.All;
        RefreshReviewQueue();
        var row = reviewRows.FirstOrDefault(item => item.Group.Occurrences.Any(occurrence => occurrence.LogicalIndex == target));
        if (row is null) return;
        DiagnosticListBox.SelectedItem = row;
        DiagnosticListBox.ScrollIntoView(row);
        var targetOccurrence = ReviewOccurrenceComboBox.Items
            .OfType<ReviewOccurrenceRow>()
            .FirstOrDefault(item => item.Occurrence.LogicalIndex == target);
        if (targetOccurrence is not null)
            ReviewOccurrenceComboBox.SelectedItem = targetOccurrence;
    }

    private void InspectorContactsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        PaintSurface.SetHighlightedContact((InspectorContactsList.SelectedItem as InspectorContactRow)?.Id);

    private void SetReviewRailCollapsed(bool collapsed)
    {
        reviewRailCollapsed = collapsed;
        WorkspaceShellGrid.ColumnDefinitions[0].Width = new GridLength(collapsed ? 0 : ExpandedReviewRailWidth);
        ReviewRailBorder.IsVisible = !collapsed;
        ReviewRailTitle.IsVisible = !collapsed;
        ReviewRailCountBadge.IsVisible = !collapsed;
        ClearMarkersButton.IsVisible = !collapsed;
        ReviewRailContent.IsVisible = !collapsed;
        ReviewRailToggleButton.Content = collapsed ? "\uE76C" : "\uE76B";
        ToolTip.SetTip(ReviewRailToggleButton, collapsed ? "Open review queue" : "Collapse review queue");
    }

    private void SetInspectorRailCollapsed(bool collapsed)
    {
        var wasCollapsed = inspectorRailCollapsed;
        inspectorRailCollapsed = collapsed;
        var column = WorkspaceShellGrid.ColumnDefinitions[3];
        if (collapsed)
        {
            if (column.ActualWidth >= MinimumInspectorRailWidth)
                expandedInspectorRailWidth = Math.Clamp(column.ActualWidth, MinimumInspectorRailWidth, MaximumInspectorRailWidth);
            column.MinWidth = 0;
            column.MaxWidth = CollapsedInspectorRailWidth;
            column.Width = new GridLength(CollapsedInspectorRailWidth);
        }
        else
        {
            if (!wasCollapsed && column.ActualWidth >= MinimumInspectorRailWidth)
                expandedInspectorRailWidth = Math.Clamp(column.ActualWidth, MinimumInspectorRailWidth, MaximumInspectorRailWidth);
            column.MinWidth = MinimumInspectorRailWidth;
            column.MaxWidth = MaximumInspectorRailWidth;
            column.Width = new GridLength(Math.Clamp(
                expandedInspectorRailWidth <= 0 ? DefaultInspectorRailWidth : expandedInspectorRailWidth,
                MinimumInspectorRailWidth,
                MaximumInspectorRailWidth));
        }
        InspectorGridSplitter.IsVisible = !collapsed;
        InspectorRailTitle.IsVisible = !collapsed;
        CopyInspectorButton.IsVisible = !collapsed;
        InspectorRailContent.IsVisible = !collapsed;
        InspectorCollapsedSummary.IsVisible = collapsed;
        InspectorRailToggleButton.Content = collapsed ? "\uE76B" : "\uE76C";
        ToolTip.SetTip(InspectorRailToggleButton, collapsed ? "Open inspector" : "Collapse inspector");
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
        PaintSurface.InvalidateVisual();
        RefreshCurrentOutputVideoFrame();
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

    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SettingsPage.IsVisible) CloseSettingsPage();
        else OpenSettingsPage();
    }

    private void EditConfigurationButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!EventVersionComboBox.IsEnabled) return;
        EventVersionComboBox.Focus();
        EventVersionComboBox.IsDropDownOpen = true;
    }

    private void OpenSettingsPage()
    {
        StopPlayback();
        StopOutputVideoPreviewPlayback();
        CloseCommandPalette();
        SetSourceTransportEnabled(false);
        SetOutputWorkspaceChrome(outputWorkspaceActive, showReplayTransport: false);
        SettingsPage.IsVisible = true;
        SettingsPage.Focus();
    }

    private void CloseSettingsPage()
    {
        SettingsPage.IsVisible = false;
        var paintSelected = ReferenceEquals(WorkspaceTabs.SelectedItem, PaintTab);
        SetOutputWorkspaceChrome(outputWorkspaceActive, paintSelected);
        SetSourceTransportEnabled(paintSelected && !outputWorkspaceActive);
        Focus();
    }

    private async void LoadButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load capture",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported captures") { Patterns = ["*.txt", "*.log", "*.csv", "*.xlsx", "*.xlsm"] },
                FilePickerFileTypes.All,
            ],
        });
        var path = files.SingleOrDefault()?.TryGetLocalPath();
        if (path is null)
        {
            return;
        }

        await OpenCaptureAsync(path);
    }

    internal async Task OpenCaptureAsync(string path, string? adapterId = null)
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, "Opening capture");
        ClearSession();

        try
        {
            var progress = new Progress<CaptureLoadProgress>(item =>
            {
                var count = item.RecordsRead > 0 ? $" · {item.RecordsRead:N0} records" : string.Empty;
                SessionStatusText.Text = item.Phase + count;
            });
            session = await Task.Run(
                () => CaptureSession.LoadAsync(path, progress, cancellationToken, adapterId),
                cancellationToken);
            var projectedActivities = await Task.Run(
                () => RegisterActivityProjector.Project(session.Records).ToArray(),
                cancellationToken);
            ApplyRawExplorerProjection(projectedActivities);
            diagnosticRows = session.SourceDiagnostics.Select(diagnostic => new DiagnosticRow(diagnostic)).ToArray();
            CreateReviewSession(session.SourceDiagnostics);
            CaptureNameText.Text = Path.GetFileName(session.SourcePath).ToUpperInvariant();
            SessionStatusText.Text = $"{session.Records.Count:N0} physical records indexed · {diagnosticRows.Length:N0} source diagnostics · semantic format required";
            SourceAdapterText.Text = session.Probe.DisplayName;
            SourceAdapterText.IsVisible = true;
            SourceAdapterComboBox.IsVisible = false;
            pendingSourcePath = null;
            SourceConfidenceText.Text = $"{session.Probe.Confidence} confidence · {session.Probe.Reasons.FirstOrDefault()}";
            SourceHashText.Text = $"SHA-256\n{session.SourceSha256}";
            EventVersionComboBox.IsEnabled = true;
            RegisterProfileComboBox.IsEnabled = true;
            EditConfigurationButton.IsEnabled = true;
            ExportReadableLogButton.IsEnabled = true;
            configuringRegisterProfile = true;
            RegisterProfileComboBox.SelectedIndex = 0;
            configuringRegisterProfile = false;
            ConfigurationHintText.Text = "Confirm the version to decode automatically; source detection never infers it.";
            SetTimelineCounts(session.Records.Count, 0, diagnosticRows.Length);
            TimelineStatusText.Text = "Raw capture indexed · confirm Event Buffer Version to open Paint";
            WorkspaceTabs.SelectedIndex = 0;
            if (allRawRows.Length > 0)
            {
                RawRecordsList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Load cancelled; no partial session was committed";
        }
        catch (SourceSelectionRequiredException exception)
        {
            pendingSourcePath = path;
            configuringSourceChoice = true;
            SourceAdapterComboBox.ItemsSource = exception.Candidates
                .Select(candidate => new SourceAdapterChoice(candidate.AdapterId, candidate.DisplayName, candidate.Confidence))
                .ToArray();
            ComboBoxAutoSizer.Fit(SourceAdapterComboBox);
            SourceAdapterComboBox.SelectedIndex = -1;
            SourceAdapterComboBox.IsVisible = true;
            SourceAdapterText.IsVisible = false;
            configuringSourceChoice = false;
            SessionStatusText.Text = "Source selection required; no adapter was chosen automatically";
            SourceConfidenceText.Text = "The highest-confidence probe is tied";
            ConfigurationHintText.Text = "Choose the correct source adapter; Event Buffer Version remains a separate decision.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SessionStatusText.Text = $"Load failed · {exception.Message}";
            ConfigurationHintText.Text = "Choose another file, inspect its schema, or select a supported adapter explicitly.";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    internal async Task ApplyStartupDecodeAsync(string eventVersion, string? palmProfile, string? registerProfile = null)
    {
        if (!string.IsNullOrWhiteSpace(registerProfile))
        {
            var profileChoice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
                .FirstOrDefault(item => item.IcFamily?.Equals(registerProfile, StringComparison.OrdinalIgnoreCase) == true);
            if (profileChoice is null)
            {
                ConfigurationHintText.Text = $"Unsupported startup IC register profile: {registerProfile}";
                return;
            }
            configuringRegisterProfile = true;
            RegisterProfileComboBox.SelectedItem = profileChoice;
            configuringRegisterProfile = false;
            await ApplyRegisterProfileAsync(profileChoice);
        }

        var normalizedVersion = eventVersion.Trim().ToUpperInvariant();
        if (!normalizedVersion.StartsWith("0X", StringComparison.Ordinal))
            normalizedVersion = $"0X{normalizedVersion}";

        var versionIndex = normalizedVersion switch
        {
            "0X82" => 0,
            "0X83" => 1,
            "0X84" => 2,
            "0X85" => 3,
            "0X97" => 4,
            _ => -1,
        };
        if (versionIndex < 0)
        {
            ConfigurationHintText.Text = $"Unsupported startup Event Buffer Version: {eventVersion}";
            return;
        }

        configuringEventVersion = true;
        try
        {
            EventVersionComboBox.SelectedIndex = versionIndex;
        }
        finally
        {
            configuringEventVersion = false;
        }
        if (versionIndex == 4)
        {
            if (string.IsNullOrWhiteSpace(registerProfile))
            {
                ConfigurationHintText.Text = "Startup 0x97 decode requires --register-profile and --palm-profile.";
                return;
            }
            var profileIndex = palmProfile?.Trim().ToUpperInvariant() switch
            {
                "STANDARD" => 0,
                "BENZ" or "BENZ PALM" or "BENZ-PALM" => 1,
                _ => -1,
            };
            if (profileIndex < 0)
            {
                ConfigurationHintText.Text = "Startup 0x97 decode requires --palm-profile Standard or Benz-Palm.";
                return;
            }
            Desay97ProfileComboBox.SelectedIndex = profileIndex;
        }

        await DecodeSelectedAsync();
    }

    private async Task DecodeSelectedAsync()
    {
        if (session is null || operationInProgress)
        {
            return;
        }

        if (EventVersionComboBox.SelectedItem is not ComboBoxItem selected ||
            selected.Content is not string versionText)
        {
            ConfigurationHintText.Text = "Confirm Event Buffer Version; decoding starts immediately after selection.";
            return;
        }

        var isDesay97 = versionText.Equals("0x97", StringComparison.OrdinalIgnoreCase);
        var registerProfile = NvtRegisterCatalog.FindProfile(session.RegisterProfile);
        Desay97Profile? desayProfile = null;
        if (isDesay97)
        {
            if (registerProfile is null)
            {
                ConfigurationHintText.Text = "Desay 0x97 requires an explicit IC profile before decoding.";
                return;
            }
            desayProfile = Desay97ProfileComboBox.SelectedItem is ComboBoxItem profileItem &&
                           profileItem.Content?.ToString() == "Benz Palm"
                ? Desay97Profile.BenzPalm
                : Desay97ProfileComboBox.SelectedItem is ComboBoxItem
                    ? Desay97Profile.Standard
                    : null;
            if (desayProfile is null)
            {
                ConfigurationHintText.Text = "Desay 0x97 requires an explicit Standard or Benz Palm selection.";
                return;
            }
        }
        else if (!CommonEventBufferDecoder.TryParseVersion(versionText, out _))
        {
            ConfigurationHintText.Text = "The selected Event Buffer Version is not supported.";
            return;
        }

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, $"Decoding {(isDesay97 ? "Desay" : "Common")} {versionText}");

        try
        {
            IReadOnlyList<ReplayDiagnostic> decodeDiagnostics;
            string formatLabel;
            if (isDesay97)
            {
                var profile = desayProfile ?? throw new InvalidOperationException("Desay profile was validated before decoding.");
                var eventBufferBase = registerProfile?.EventBufferBase ??
                    throw new InvalidOperationException("Desay IC profile was validated before decoding.");
                var report = await Task.Run(() => session.DecodeDesay97(profile, eventBufferBase), cancellationToken);
                replaySession = await Task.Run(() => new Desay97ReplaySession(report.Frames), cancellationToken);
                decodedRows = report.Frames.Select((frame, index) => DecodedFrameRow.FromDesay97(index, frame)).ToArray();
                decodeDiagnostics = report.Diagnostics;
                formatLabel = $"Desay 0x97 / {ProfileText(profile)}";
            }
            else
            {
                CommonEventBufferDecoder.TryParseVersion(versionText, out var version);
                var report = await Task.Run(() => session.DecodeCommon(version), cancellationToken);
                replaySession = await Task.Run(() => new CommonReplaySession(report.Frames), cancellationToken);
                decodedRows = report.Frames.Select((frame, index) => DecodedFrameRow.FromCommon(index, frame)).ToArray();
                decodeDiagnostics = report.Diagnostics;
                formatLabel = $"Common {versionText}";
            }

            var nextConfiguration = new ReplayDecodeConfiguration(
                versionText,
                isDesay97 ? ProfileText(desayProfile!.Value) : null,
                session.Probe.AdapterId,
                registerProfile?.EventBufferBase ?? 0,
                session.RegisterProfile);
            if (decodeConfiguration is not null && decodeConfiguration != nextConfiguration)
                markers.Clear();
            decodeConfiguration = nextConfiguration;

            decodedRowsBySourceId = decodedRows
                .SelectMany(row => row.PhysicalRecords.Append(row.Source).Select(source => (source.StableId, Row: row)))
                .GroupBy(item => item.StableId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Row, StringComparer.Ordinal);
            diagnosticRows = await Task.Run(
                () => decodeDiagnostics
                    .Concat(replaySession.Diagnostics)
                    .Select(diagnostic => new DiagnosticRow(diagnostic))
                    .ToArray(),
                cancellationToken);
            CreateReviewSession(diagnosticRows.Select(row => row.Diagnostic).ToArray());

            DecodedFramesList.ItemsSource = decodedRows;
            SessionStatusText.Text = $"{formatLabel} · {decodedRows.Length:N0} frames · {diagnosticRows.Length:N0} findings";
            ConfigurationHintText.Text = $"{formatLabel} confirmed · decoded automatically · raw source remains unchanged";
            SetTimelineCounts(session.Records.Count, decodedRows.Length, diagnosticRows.Length);
            TimelineStatusText.Text = decodedRows.Length > 0
                ? "Logical replay ready · Space play/pause · ←/→ step · drag Loop handles"
                : "No replayable event-buffer frames · inspect physical records and Review Queue";
            InitializeReplay();
            SaveReviewButton.IsEnabled = true;
            LoadReviewButton.IsEnabled = true;
            ExportAnalysisButton.IsEnabled = true;
            AnalysisTab.IsEnabled = true;
            AnalysisSummaryText.Text = $"Ready · {decodedRows.Length:N0} frames · export the full replay or current In/Out range";
            AddMarkerButton.IsEnabled = decodedRows.Length > 0;
            if (decodedRows.Length > 0)
            {
                WorkspaceTabs.SelectedItem = PaintTab;
                outputWorkspaceActive = false;
                SetOutputWorkspaceChrome(false, showReplayTransport: true);
                SetSourceTransportEnabled(true);
                SeekReplay(0);
            }
            else
            {
                WorkspaceTabs.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Decode cancelled; previous complete result was preserved";
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            SessionStatusText.Text = $"Decode failed · {exception.Message}";
            ConfigurationHintText.Text = "Raw records remain available; verify version/profile and try again.";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
    }

    private void OutputContentComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutputContentComboBox?.SelectedItem is not SelectOption option ||
            OutputVideoPanel is null || OutputHeatmapPanel is null || OutputPackagePanel is null)
            return;

        var mp4 = option.Value == "mp4";
        var heatmap = option.Value == "heatmap";
        OutputVideoPanel.IsVisible = mp4;
        OutputHeatmapPanel.IsVisible = heatmap;
        OutputPackagePanel.IsVisible = option.Value == "package";
        OutputModeTitleText.Text = option.Label.ToUpperInvariant();
        ExportSelectedOutputButton.Content = option.Value switch
        {
            "heatmap" => "Export PNG",
            "package" => "Export package",
            _ => "Export MP4",
        };
        OutputContentDescriptionText.Text = option.Description;
        AutomationProperties.SetName(ExportSelectedOutputButton, ExportSelectedOutputButton.Content?.ToString());
        if (!mp4) StopOutputVideoPreviewPlayback();
        if (!mp4) OutputExportWarningPanel.IsVisible = false;
        if (heatmap) RefreshHeatmapPresentation();
        UpdateOutputExportButtonAvailability();
    }

    private void HeatmapSetting_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HeatmapRepeatedThresholdPanel is null) return;
        HeatmapRepeatedThresholdPanel.IsVisible = SelectedHeatmapMode() == "repeated";
        ApplyHeatmapSettings();
    }

    private void HeatmapSetting_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyHeatmapSettings();
        e.Handled = true;
    }

    private void HeatmapSetting_OnLostFocus(object? sender, RoutedEventArgs e) => ApplyHeatmapSettings();

    private void ApplyHeatmapSettings()
    {
        if (!int.TryParse(HeatmapRepeatedThresholdTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimumCount) ||
            minimumCount is < 1 or > 1_000_000)
        {
            SessionStatusText.Text = "Repeated hotspot minimum N must be between 1 and 1,000,000.";
            return;
        }
        if (!double.TryParse(HeatmapLabelThresholdTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var labelPercent) ||
            labelPercent is < 0 or > 100)
        {
            SessionStatusText.Text = "Heatmap value label threshold must be between 0% and 100%.";
            return;
        }
        var selectedMode = SelectedHeatmapMode();
        var nextLabelRatio = labelPercent / 100d;
        if (minimumCount == heatmapMinimumCount &&
            Math.Abs(nextLabelRatio - heatmapLabelThresholdRatio) < 0.0001 &&
            string.Equals(selectedMode, appliedHeatmapMode, StringComparison.Ordinal))
            return;
        heatmapMinimumCount = minimumCount;
        heatmapLabelThresholdRatio = nextLabelRatio;
        appliedHeatmapMode = selectedMode;
        RefreshHeatmapPresentation();
    }

    private string SelectedHeatmapMode() =>
        (HeatmapModeComboBox.SelectedItem as SelectOption)?.Value ?? "all";

    private void ExportSelectedOutputButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var value = (OutputContentComboBox.SelectedItem as SelectOption)?.Value;
        if (value == "package")
            ExportAnalysisButton_OnClick(sender, e);
        else if (value == "heatmap")
            ExportHeatmapButton_OnClick(sender, e);
        else
            ExportReplayButton_OnClick(sender, e);
    }

    private async void ExportHeatmapButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || replaySession.Count == 0) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export touch density heatmap",
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.SourcePath) + ".heatmap.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, "Rendering heatmap PNG");
        try
        {
            var report = await Task.Run(
                () => BuildOutputReport(SelectedOutputRange(), cancellationToken),
                cancellationToken);
            currentOutputReport = report;
            RefreshHeatmapPresentation();
            await WriteCurrentHeatmapPreviewAsync(path, cancellationToken);
            var mode = SelectedHeatmapMode() == "repeated" ? $"repeated N≥{heatmapMinimumCount:N0}" : "all recorded points";
            AnalysisOutputText.Text = $"Heatmap PNG · {path}\n{AnalysisHeatmapPreview.VisibleSampleCount:N0} visible samples · {mode} · range {report.Manifest.Range.StartLogicalIndex + 1:N0}-{report.Manifest.Range.EndLogicalIndex + 1:N0}";
            SessionStatusText.Text = "Heatmap PNG exported";
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Heatmap export cancelled; no partial output was kept";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            SessionStatusText.Text = $"Heatmap export failed · {exception.Message}";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private async Task WriteCurrentHeatmapPreviewAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisHeatmapPreview.ClearHover();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var width = Math.Max(640, (int)Math.Ceiling(AnalysisHeatmapPreview.Bounds.Width));
        var height = Math.Max(360, (int)Math.Ceiling(AnalysisHeatmapPreview.Bounds.Height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(AnalysisHeatmapPreview);
        await using var memory = new MemoryStream();
        bitmap.Save(memory, PngBitmapEncoderOptions.Default);
        var png = memory.ToArray();
        await AtomicOutput.WriteAsync(
            path,
            (stream, token) => stream.WriteAsync(png, token).AsTask(),
            cancellationToken);
    }

    private async void ExportAnalysisButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewSession is null || replaySession.Count == 0) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export analysis outputs",
            AllowMultiple = false,
        });
        var directory = folders.SingleOrDefault()?.TryGetLocalPath();
        if (directory is null) return;

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, "Analyzing selected replay range");
        try
        {
            var range = SelectedOutputRange();
            var report = await Task.Run(() => BuildOutputReport(range, cancellationToken), cancellationToken);
            var result = await new AnalysisOutputWriter().WriteAsync(directory, report, cancellationToken, session.Records);
            AnalysisSummaryText.Text =
                $"{report.Events.Count:N0} frames · {report.DiagnosticAggregates.Count:N0} finding groups · " +
                $"{report.Asil.Assertions} ASIL assert / {report.Asil.Clears} clear · {report.Hotspot.SampleCount:N0} hotspot samples";
            AnalysisOutputText.Text =
                $"{result.Directory}\nanalysis-report.json · events.json/csv · diagnostics.json/csv · communication-readable.csv/jsonl · manifest.json · heatmap.png\n" +
                $"clock={report.Manifest.Clock.Domain} · range={range.StartLogicalIndex + 1}:{range.EndLogicalIndex + 1} · SHA-256={report.Manifest.SourceSha256}";
            WorkspaceTabs.SelectedItem = AnalysisTab;
            SessionStatusText.Text = $"Analysis exported atomically · {report.Events.Count:N0} frames";
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Analysis cancelled; prior complete output was preserved";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SessionStatusText.Text = $"Analysis export failed · {exception.Message}";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private async void ExportReplayButton_OnClick(object? sender, RoutedEventArgs e) =>
        await BeginReplayExportAsync(longExportConfirmed: false);

    private async void OutputExportContinueButton_OnClick(object? sender, RoutedEventArgs e) =>
        await BeginReplayExportAsync(longExportConfirmed: true);

    private void OutputExportDismissButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OutputExportWarningPanel.IsVisible = false;
        SessionStatusText.Text = "Long MP4 export not started";
    }

    private void OutputExportCancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (outputExportCancellation is null) return;
        OutputExportCancelButton.IsEnabled = false;
        OutputExportStatusText.Text = "Cancelling MP4 export…";
        outputExportCancellation.Cancel();
    }

    private async Task BeginReplayExportAsync(bool longExportConfirmed)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || replaySession.Count == 0 || outputExportInProgress) return;
        var range = SelectedOutputRange();
        var preflightOptions = CreateReplayExportOptions("preflight.mp4", range);
        var preflightPlan = ReplayFramePlan.Build(replaySession, preflightOptions);
        var estimatedFrames = ReplayFramePlan.OutputFrameCount(preflightPlan);
        var estimatedDuration = TimeSpan.FromSeconds(estimatedFrames / (double)outputVideoFrameRate);
        if (!longExportConfirmed && RequiresLongVideoExport(estimatedFrames, estimatedDuration))
        {
            OutputExportWarningText.Text =
                $"This selection produces {estimatedFrames:N0} frames ({FormatClock(estimatedDuration)}) at " +
                $"{outputVideoFrameRate} FPS and {outputVideoWidth} × {outputVideoHeight}. " +
                "Recorded gaps can substantially increase export time and file size.";
            OutputExportWarningPanel.IsVisible = true;
            SessionStatusText.Text = "Review long MP4 export estimate";
            return;
        }

        OutputExportWarningPanel.IsVisible = false;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export replay MP4",
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.SourcePath) + ".replay.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("MPEG-4 video") { Patterns = ["*.mp4"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        var exportReplay = replaySession;
        var exportExtent = replayExtent;
        var exportDiagnostics = reviewSession?.Diagnostics.ToArray();
        var exportMarkers = markers.ToArray();
        var exportTrailHistory = trailHistory;
        var exportTrailMode = trailMode;
        var exportTrailLength = trailLength;
        var exportTrailVisibilityStart = trailVisibilityStart;
        var exportTrailVisible = traceVisible || trailPointsVisible;
        var exportReverseX = reverseX;
        var exportReverseY = reverseY;
        var exportSwapAxes = swapAxes;
        ReplayScene ExportScene(int logicalIndex) => ReplaySceneFactory.Create(
            exportReplay.Seek(logicalIndex),
            exportReplay.Count,
            exportExtent,
            exportDiagnostics,
            exportMarkers,
            exportTrailVisible
                ? exportTrailHistory?.Build(logicalIndex, exportTrailMode, exportTrailLength, exportTrailVisibilityStart) ?? []
                : [],
            exportReverseX,
            exportReverseY,
            exportSwapAxes);

        outputExportCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        outputExportCancellation = cancellation;
        var cancellationToken = cancellation.Token;
        SetOutputExportState(true, estimatedFrames);
        var progress = new Progress<ReplayExportProgress>(item =>
        {
            if (!ReferenceEquals(outputExportCancellation, cancellation)) return;
            OutputExportProgressBar.IsIndeterminate = item.TotalFrames == 0;
            OutputExportProgressBar.Value = item.Percent;
            OutputExportStatusText.Text = item.Stage;
            OutputExportProgressText.Text = item.TotalFrames == 0
                ? "Preparing"
                : $"{item.Percent:0}% · {item.CompletedFrames:N0}/{item.TotalFrames:N0}";
        });
        try
        {
            var options = CreateReplayExportOptions(path, range);
            var result = await Task.Run(
                () => new ReplayRangeExporter().ExportAsync(exportReplay, ExportScene, options, cancellationToken, progress),
                cancellationToken);
            AnalysisOutputText.Text =
                $"{result.Kind} · {result.OutputPath}\n{result.OutputFrameCount:N0} frames · {result.Duration.TotalSeconds:0.###} s\nmanifest={result.ManifestPath}" +
                (result.Warning is null ? string.Empty : $"\nWARNING: {result.Warning}");
            SessionStatusText.Text = result.Kind == ReplayExportKind.Mp4
                ? "Replay MP4 exported"
                : "FFmpeg unavailable · PNG sequence exported safely";
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Replay export cancelled; no partial output was kept";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            SessionStatusText.Text = $"Replay export failed · {exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(outputExportCancellation, cancellation))
            {
                outputExportCancellation = null;
                cancellation.Dispose();
                SetOutputExportState(false, 0);
            }
        }
    }

    internal static bool RequiresLongVideoExport(int frameCount, TimeSpan duration) =>
        frameCount >= 10_000 || duration >= TimeSpan.FromMinutes(2);

    private void SetOutputExportState(bool exporting, int totalFrames)
    {
        outputExportInProgress = exporting;
        LoadButton.IsEnabled = !exporting && !operationInProgress;
        OutputExportActivityPanel.IsVisible = exporting;
        OutputExportCancelButton.IsEnabled = exporting;
        if (exporting)
        {
            OutputExportProgressBar.IsIndeterminate = true;
            OutputExportProgressBar.Value = 0;
            OutputExportStatusText.Text = "Preparing MP4 export";
            OutputExportProgressText.Text = totalFrames > 0 ? $"0% · 0/{totalFrames:N0}" : "Preparing";
        }
        UpdateOutputExportButtonAvailability();
    }

    private void UpdateOutputExportButtonAvailability()
    {
        if (ExportSelectedOutputButton is null) return;
        var mp4Selected = (OutputContentComboBox.SelectedItem as SelectOption)?.Value == "mp4";
        ExportSelectedOutputButton.IsEnabled =
            !operationInProgress && replaySession is { Count: > 0 } && (!mp4Selected || !outputExportInProgress);
    }

    private async void EventVersionComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var comboBox = sender as ComboBox;
        var isDesay97 = comboBox?.SelectedItem is ComboBoxItem item &&
                        item.Content?.ToString() == "0x97";
        if (Desay97ProfileComboBox is null)
        {
            return;
        }

        Desay97ProfileComboBox.IsVisible = isDesay97;
        if (!isDesay97)
        {
            Desay97ProfileComboBox.SelectedIndex = -1;
        }
        if (session is not null)
        {
            ConfigurationHintText.Text = isDesay97
                ? NvtRegisterCatalog.FindProfile(session.RegisterProfile) is null
                    ? $"0x97 pending · {ActiveDecodeContext()} · select IC profile, then Standard or Benz Palm."
                    : $"0x97 pending · {ActiveDecodeContext()} · select Standard or Benz Palm."
                : "Version confirmed · decoding automatically; source detection did not infer it.";
        }
        if (!configuringEventVersion && session is not null && !isDesay97 &&
            comboBox is { SelectedIndex: >= 0, IsDropDownOpen: false } &&
            !SelectedDecodeConfigurationMatchesActive())
            await DecodeSelectedAsync();
    }

    private void OpenOutputPreviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (AnalysisTab.IsEnabled)
            WorkspaceTabs.SelectedItem = AnalysisTab;
    }

    private void OutputVideoSetting_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringOutputSettings || OutputClockComboBox is null || OutputSpeedComboBox is null ||
            OutputFrameRateComboBox is null || OutputResolutionComboBox is null)
            return;

        var previousSettings = (outputVideoClock, outputVideoSpeed, outputVideoFrameRate, outputVideoWidth, outputVideoHeight);
        OutputExportWarningPanel.IsVisible = false;

        outputVideoClock = SelectedTag(OutputClockComboBox) == "recorded"
            ? ReplayExportClock.Recorded
            : ReplayExportClock.Frame;
        OutputClockDescriptionText.Text = outputVideoClock == ReplayExportClock.Recorded
            ? "Follows source timestamps, including long idle gaps with no touch."
            : "Plays every decoded frame at 120 Hz; REC labels keep their original timestamps.";

        var speedTag = SelectedTag(OutputSpeedComboBox);
        OutputCustomSpeedTextBox.IsVisible = speedTag == "custom";
        if (speedTag != "custom" && double.TryParse(speedTag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
            outputVideoSpeed = speed;

        var frameRateTag = SelectedTag(OutputFrameRateComboBox);
        OutputCustomFrameRateTextBox.IsVisible = frameRateTag == "custom";
        if (int.TryParse(frameRateTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameRate))
            outputVideoFrameRate = frameRate;
        else if (frameRateTag == "custom" &&
                 int.TryParse(OutputCustomFrameRateTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var customFrameRate) &&
                 customFrameRate is >= 1 and <= 240)
            outputVideoFrameRate = customFrameRate;

        var resolutionTag = SelectedTag(OutputResolutionComboBox);
        var customResolution = resolutionTag == "custom";
        OutputCustomResolutionLabel.IsVisible = customResolution;
        OutputCustomResolutionPanel.IsVisible = customResolution;
        if (resolutionTag == "panel")
        {
            SyncOutputResolutionWithPanel();
        }
        else if (!customResolution && TryParseOutputResolution(resolutionTag, out var width, out var height))
        {
            outputVideoWidth = width;
            outputVideoHeight = height;
            OutputWidthTextBox.Text = width.ToString(CultureInfo.InvariantCulture);
            OutputHeightTextBox.Text = height.ToString(CultureInfo.InvariantCulture);
        }

        var nextSettings = (outputVideoClock, outputVideoSpeed, outputVideoFrameRate, outputVideoWidth, outputVideoHeight);
        if (nextSettings == previousSettings) return;
        StopOutputVideoPreviewPlayback();
        RefreshOutputPreviewIfVisible();
    }

    private void OutputCustomSetting_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyOutputCustomSettings();
        e.Handled = true;
    }

    private void OutputCustomSetting_OnLostFocus(object? sender, RoutedEventArgs e) => ApplyOutputCustomSettings();

    private void ApplyOutputCustomSettings()
    {
        var previousSettings = (outputVideoSpeed, outputVideoFrameRate, outputVideoWidth, outputVideoHeight);
        if (OutputSpeedComboBox.SelectedItem is ComboBoxItem speedItem &&
            string.Equals(speedItem.Tag?.ToString(), "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (!double.TryParse(OutputCustomSpeedTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) ||
                speed is <= 0 or > 100)
            {
                SessionStatusText.Text = "MP4 speed must be greater than 0 and no more than 100×.";
                return;
            }
            outputVideoSpeed = speed;
        }

        if (OutputFrameRateComboBox.SelectedItem is ComboBoxItem frameRateItem &&
            string.Equals(frameRateItem.Tag?.ToString(), "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(OutputCustomFrameRateTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frameRate) ||
                frameRate is < 1 or > 240)
            {
                SessionStatusText.Text = "MP4 frame rate must be between 1 and 240 FPS.";
                return;
            }
            outputVideoFrameRate = frameRate;
        }

        if (OutputResolutionComboBox.SelectedItem is ComboBoxItem resolutionItem &&
            string.Equals(resolutionItem.Tag?.ToString(), "custom", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(OutputWidthTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
                !int.TryParse(OutputHeightTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) ||
                width is < 320 or > 7680 || height is < 180 or > 4320 || width % 2 != 0 || height % 2 != 0)
            {
                SessionStatusText.Text = "MP4 size must use even values: width 320-7680 and height 180-4320.";
                return;
            }
            outputVideoWidth = width;
            outputVideoHeight = height;
        }

        var nextSettings = (outputVideoSpeed, outputVideoFrameRate, outputVideoWidth, outputVideoHeight);
        if (nextSettings == previousSettings) return;
        SessionStatusText.Text = $"MP4 preview settings · {OutputVideoSettingsLabel()}";
        StopOutputVideoPreviewPlayback();
        RefreshOutputPreviewIfVisible();
    }

    private static string? SelectedTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static bool TryParseOutputResolution(string? text, out int width, out int height)
    {
        width = 0;
        height = 0;
        var parts = text?.Split('x', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 2 } &&
               int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) &&
               int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
    }

    private void SyncOutputResolutionWithPanel()
    {
        var width = NormalizeVideoDimension(replayExtent.MaximumX, 320, 7680);
        var height = NormalizeVideoDimension(replayExtent.MaximumY, 180, 4320);
        OutputPanelResolutionItem.Content = $"Paint · {width} × {height}";
        if (OutputResolutionComboBox.SelectedItem is not ComboBoxItem item || item.Tag?.ToString() != "panel")
            return;
        outputVideoWidth = width;
        outputVideoHeight = height;
        OutputWidthTextBox.Text = width.ToString(CultureInfo.InvariantCulture);
        OutputHeightTextBox.Text = height.ToString(CultureInfo.InvariantCulture);
    }

    private static int NormalizeVideoDimension(double value, int minimum, int maximum)
    {
        var result = (int)Math.Clamp(Math.Round(value), minimum, maximum);
        return result % 2 == 0 ? result : Math.Min(maximum, result + 1);
    }

    private string OutputVideoSettingsLabel() =>
        $"{outputVideoWidth} × {outputVideoHeight} / {outputVideoFrameRate} FPS / {outputVideoSpeed:0.##}×";

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
            }
            SetOutputWorkspaceChrome(false, paintSelected && !SettingsPage.IsVisible);
            SetSourceTransportEnabled(paintSelected && !SettingsPage.IsVisible);
            if (paintSelected && replaySession is { Count: > 0 })
                TimelineStatusText.Text = "Paused · Space play/pause · ←/→ step · drag Loop handles to set range";
            ApplyResponsiveRails(Bounds.Width);
        }
    }

    private AnalysisRange SelectedOutputRange()
    {
        if (replaySession is null || replaySession.Count == 0)
            return new AnalysisRange(0, 0);
        return loopIn is { } start && loopOut is { } end
            ? new AnalysisRange(Math.Min(start, end), Math.Max(start, end))
            : new AnalysisRange(0, replaySession.Count - 1);
    }

    private ReplayExportOptions CreateReplayExportOptions(string path, AnalysisRange range)
    {
        if (session is null || decodeConfiguration is null)
            throw new InvalidOperationException("A decoded capture is required for MP4 output.");
        return new ReplayExportOptions(
            path,
            range,
            outputVideoWidth,
            outputVideoHeight,
            outputVideoFrameRate,
            outputVideoSpeed,
            outputVideoClock,
            PaintSurface.Mode,
            SourceFileName: Path.GetFileName(session.SourcePath),
            SourceSha256: session.SourceSha256,
            DecodeConfiguration: decodeConfiguration,
            RenderSettings: CurrentReplayRenderSettings());
    }

    private ReplayRenderSettings CurrentReplayRenderSettings() => new(
        PaintSurface.Mode,
        Application.Current?.ActualThemeVariant == ThemeVariant.Light
            ? ReplayRenderTheme.Light
            : ReplayRenderTheme.Dark,
        PaintSurface.StrongGrid,
        PaintSurface.LegendVisible,
        PaintSurface.LegendCollapsed,
        PaintSurface.LegendPosition,
        ShowTrailLines: traceVisible,
        ShowTrailPoints: trailPointsVisible);

    private ReplayScene CreateReplayScene(int logicalIndex)
    {
        if (replaySession is null || replayFrames is null) throw new InvalidOperationException("A decoded replay is required.");
        return ReplaySceneFactory.Create(
            replayFrames[logicalIndex],
            replaySession.Count,
            replayExtent,
            reviewSession?.Diagnostics,
            markers,
            traceVisible || trailPointsVisible
                ? trailHistory?.Build(logicalIndex, trailMode, trailLength, trailVisibilityStart) ?? []
                : [],
            reverseX,
            reverseY,
            swapAxes);
    }

    private CaptureAnalysisReport BuildOutputReport(AnalysisRange range, CancellationToken cancellationToken)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewSession is null)
            throw new InvalidOperationException("A decoded replay is required for output preview.");
        var evidence = decodeConfiguration.EventBufferVersion is "0x83" or "0x84"
            ? EvidenceStatus.Verified
            : EvidenceStatus.Provisional;
        return new CaptureAnalyzer().Analyze(
            session.SourcePath,
            session.SourceSha256,
            decodeConfiguration,
            evidence,
            replaySession,
            reviewSession.Diagnostics,
            reviewSession,
            range,
            cancellationToken: cancellationToken,
            snapshots: replayFrames?.Snapshots);
    }

    private async Task RefreshOutputPreviewAsync()
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewSession is null || replaySession.Count == 0)
            return;

        StopOutputVideoPreviewPlayback();
        outputPreviewCancellation?.Cancel();
        outputPreviewCancellation?.Dispose();
        outputPreviewCancellation = new CancellationTokenSource();
        var cancellationToken = outputPreviewCancellation.Token;
        var range = SelectedOutputRange();
        AnalysisSummaryText.Text = $"Preparing frames {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}...";
        OutputHotspotText.Text = "Analyzing coordinates";
        try
        {
            var report = await Task.Run(() => BuildOutputReport(range, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyOutputPreview(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            currentOutputReport = null;
            AnalysisSummaryText.Text = "Output preview unavailable";
            OutputHotspotText.Text = exception.Message;
            AnalysisHeatmapPreview.Show(null);
            ClearOutputVideoPreview();
        }
    }

    private void ApplyOutputPreview(CaptureAnalysisReport report)
    {
        if (session is null || decodeConfiguration is null) return;
        currentOutputReport = report;
        var range = report.Manifest.Range;
        var clockIsFrame = outputVideoClock == ReplayExportClock.Frame;
        var clockName = clockIsFrame ? "Frame-paced 120 Hz" : "Recorded gaps";
        var profile = string.IsNullOrWhiteSpace(decodeConfiguration.Desay97Profile)
            ? string.Empty
            : $" / {decodeConfiguration.Desay97Profile}";
        var speed = $"{outputVideoSpeed:0.##}×";

        AnalysisSummaryText.Text = $"Previewing frames {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}";
        OutputRangeText.Text = $"{report.Events.Count:N0} frames\n{range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}";
        OutputFindingsText.Text = $"{report.DiagnosticAggregates.Count:N0} groups\n{report.Asil.UnresolvedAlarmGroups:N0} alarms";
        OutputConfigurationText.Text =
            $"decoder  {decodeConfiguration.EventBufferVersion}{profile}\n" +
            $"source   {session.Probe.DisplayName}\n" +
            $"evidence {report.Manifest.FormatEvidenceStatus}\n" +
            $"paint    {PaintSurface.Mode}\n" +
            $"replay   {clockName} at {speed}";
        OutputSourceText.Text =
            $"{Path.GetFileName(session.SourcePath)}\nSHA-256\n{FormatSha256(session.SourceSha256)}";
        RefreshHeatmapPresentation();
        PrepareOutputVideoPreview(range, clockName, speed);
    }

    private void RefreshHeatmapPresentation()
    {
        if (currentOutputReport is not { } report)
        {
            AnalysisHeatmapPreview.Show(null);
            return;
        }

        var repeated = SelectedHeatmapMode() == "repeated";
        var minimum = repeated ? heatmapMinimumCount : 1;
        AnalysisHeatmapPreview.Show(report.Hotspot, minimum, heatmapLabelThresholdRatio);
        var hiddenSamples = Math.Max(0, report.Hotspot.SampleCount - AnalysisHeatmapPreview.VisibleSampleCount);
        OutputHotspotText.Text = repeated
            ? $"N≥{minimum:N0} · {AnalysisHeatmapPreview.VisibleSampleCount:N0}/{report.Hotspot.SampleCount:N0} samples · peak {AnalysisHeatmapPreview.PeakCount:N0}"
            : $"{AnalysisHeatmapPreview.VisibleSampleCount:N0} samples · {AnalysisHeatmapPreview.VisibleCellCount:N0} cells · peak {AnalysisHeatmapPreview.PeakCount:N0}";
        HeatmapRangeText.Text =
            $"Frames {report.Manifest.Range.StartLogicalIndex + 1:N0}-{report.Manifest.Range.EndLogicalIndex + 1:N0} · " +
            $"{report.Hotspot.Columns} × {report.Hotspot.Rows} grid" +
            (hiddenSamples > 0 ? $" · {hiddenSamples:N0} samples below N" : string.Empty);
    }

    private void PrepareOutputVideoPreview(AnalysisRange range, string clockName, string speed)
    {
        if (replaySession is null) return;
        var options = CreateReplayExportOptions("preview.mp4", range);
        outputVideoPlan = ReplayFramePlan.Build(replaySession, options);
        outputVideoFrameCount = ReplayFramePlan.OutputFrameCount(outputVideoPlan);
        outputVideoFrameIndex = 0;
        var duration = TimeSpan.FromSeconds(outputVideoFrameCount / (double)outputVideoFrameRate);
        OutputClockText.Text = $"{FormatClock(duration)}\n{outputVideoFrameCount:N0} frames at {outputVideoFrameRate} FPS";
        OutputVideoBadgeText.Text = OutputVideoSettingsLabel();
        OutputVideoTimeline.SetFrameCount(outputVideoFrameCount, outputVideoFrameRate);
        OutputVideoTimeline.SetSourceRange(replaySession.Count, range.StartLogicalIndex, range.EndLogicalIndex);
        OutputFullscreenTimeline.SetFrameCount(outputVideoFrameCount, outputVideoFrameRate);
        OutputPreviewPlayPauseButton.IsEnabled = outputVideoFrameCount > 0;
        OutputFullscreenPlayPauseButton.IsEnabled = outputVideoFrameCount > 0;
        OutputPreviewFullscreenButton.IsEnabled = outputVideoFrameCount > 0;
        ShowOutputVideoFrame(0);
        OutputConfigurationText.Text += $"\nvideo    {clockName} / {outputVideoFrameRate} FPS / {speed}";
    }

    private void ShowOutputVideoFrame(int outputFrameIndex)
    {
        if (replaySession is null || outputVideoFrameCount == 0 || outputVideoPlan.Count == 0) return;
        outputVideoFrameIndex = Math.Clamp(outputFrameIndex, 0, outputVideoFrameCount - 1);
        var logicalIndex = ReplayFramePlan.LogicalIndexAt(outputVideoPlan, outputVideoFrameIndex);
        var scene = CreateReplayScene(logicalIndex);
        var settings = CurrentReplayRenderSettings();
        OutputVideoPreview.Show(scene, settings, outputVideoWidth, outputVideoHeight);
        if (outputFullscreenActive)
            OutputFullscreenVideoPreview.Show(scene, settings, outputVideoWidth, outputVideoHeight);
        OutputVideoTimeline.SetPosition(outputVideoFrameIndex);
        OutputFullscreenTimeline.SetPosition(outputVideoFrameIndex);
        var currentTime = TimeSpan.FromSeconds(outputVideoFrameIndex / (double)outputVideoFrameRate);
        var duration = TimeSpan.FromSeconds(outputVideoFrameCount / (double)outputVideoFrameRate);
        OutputPreviewClockText.Text = $"{FormatClock(currentTime)} / {FormatClock(duration)}";
        OutputPreviewFrameText.Text = $"output {outputVideoFrameIndex + 1:N0}/{outputVideoFrameCount:N0}  source {logicalIndex + 1:N0}/{replaySession.Count:N0}";
        OutputFullscreenClockText.Text = OutputPreviewClockText.Text;
        OutputFullscreenFrameText.Text = $"{outputVideoFrameIndex + 1:N0} / {outputVideoFrameCount:N0}";
    }

    private void OutputVideoTimeline_OnSeekRequested(object? sender, OutputVideoSeekEventArgs e)
    {
        StopOutputVideoPreviewPlayback();
        ShowOutputVideoFrame(e.OutputFrameIndex);
    }

    private void OutputVideoTimeline_OnRangeChanged(object? sender, OutputVideoRangeEventArgs e)
    {
        loopIn = e.StartLogicalIndex;
        loopOut = e.EndLogicalIndex;
        UpdateLoopText();
    }

    private void OutputPreviewFullscreenButton_OnClick(object? sender, RoutedEventArgs e) => EnterOutputFullscreen();

    private void OutputFullscreenExitButton_OnClick(object? sender, RoutedEventArgs e) => ExitOutputFullscreen();

    private void OutputFullscreenOverlay_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        ExitOutputFullscreen();
        e.Handled = true;
    }

    private void EnterOutputFullscreen()
    {
        if (outputVideoFrameCount == 0 || outputFullscreenActive) return;
        outputFullscreenActive = true;
        outputFullscreenPreviousWindowState = WindowState;
        OutputFullscreenOverlay.IsVisible = true;
        WindowState = WindowState.FullScreen;
        ShowOutputVideoFrame(outputVideoFrameIndex);
        OutputFullscreenOverlay.Focus();
    }

    private void ExitOutputFullscreen()
    {
        if (!outputFullscreenActive) return;
        outputFullscreenActive = false;
        OutputFullscreenOverlay.IsVisible = false;
        OutputFullscreenVideoPreview.Clear();
        WindowState = outputFullscreenPreviousWindowState;
    }

    private async void OutputPreviewPlayPauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (outputVideoFrameCount == 0) return;
        if (outputVideoPlaybackCancellation is not null)
        {
            StopOutputVideoPreviewPlayback();
            return;
        }
        var cancellation = new CancellationTokenSource();
        outputVideoPlaybackCancellation = cancellation;
        SetOutputVideoPlaybackVisualState(true);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (outputVideoFrameIndex >= outputVideoFrameCount - 1)
                    ShowOutputVideoFrame(0);

                var firstOutputFrame = outputVideoFrameIndex;
                var playbackStarted = Stopwatch.GetTimestamp();
                while (outputVideoFrameIndex < outputVideoFrameCount - 1)
                {
                    var elapsed = Stopwatch.GetElapsedTime(playbackStarted).TotalSeconds;
                    var targetFrame = Math.Min(
                        outputVideoFrameCount - 1,
                        firstOutputFrame + (int)Math.Floor(elapsed * outputVideoFrameRate));
                    if (targetFrame > outputVideoFrameIndex)
                        ShowOutputVideoFrame(targetFrame);
                    if (targetFrame >= outputVideoFrameCount - 1) break;

                    var nextFrameAt = (targetFrame - firstOutputFrame + 1d) / outputVideoFrameRate;
                    var delay = Math.Max(1, (int)Math.Round((nextFrameAt - elapsed) * 1000));
                    await Task.Delay(delay, cancellation.Token);
                }

                if (OutputPreviewRepeatToggleButton.IsChecked != true) break;
                await Task.Delay(Math.Max(1, 1000 / outputVideoFrameRate), cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(outputVideoPlaybackCancellation, cancellation))
            {
                outputVideoPlaybackCancellation = null;
                cancellation.Dispose();
                SetOutputVideoPlaybackVisualState(false);
            }
        }
    }

    private void StopOutputVideoPreviewPlayback()
    {
        var cancellation = outputVideoPlaybackCancellation;
        outputVideoPlaybackCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (OutputPreviewPlayPauseButton is not null)
            SetOutputVideoPlaybackVisualState(false);
    }

    private void SetOutputVideoPlaybackVisualState(bool playing)
    {
        OutputPreviewPlayPauseButton.Classes.Set("playing", playing);
        OutputFullscreenPlayPauseButton.Classes.Set("playing", playing);
        OutputPreviewPlayPauseButton.Content = playing ? "\uE769" : "\uE768";
        OutputFullscreenPlayPauseButton.Content = playing ? "Ⅱ  Pause" : "▶  Play";
        AutomationProperties.SetName(OutputPreviewPlayPauseButton, playing ? "Pause MP4 preview" : "Play MP4 preview");
        AutomationProperties.SetName(OutputFullscreenPlayPauseButton, playing ? "Pause full-screen MP4 preview" : "Play full-screen MP4 preview");
    }

    private void ClearOutputVideoPreview()
    {
        ExitOutputFullscreen();
        StopOutputVideoPreviewPlayback();
        outputVideoPlan = [];
        outputVideoFrameCount = 0;
        outputVideoFrameIndex = 0;
        OutputVideoPreview.Clear();
        OutputFullscreenVideoPreview.Clear();
        OutputVideoTimeline.SetFrameCount(0, outputVideoFrameRate);
        OutputFullscreenTimeline.SetFrameCount(0, outputVideoFrameRate);
        OutputPreviewPlayPauseButton.IsEnabled = false;
        OutputFullscreenPlayPauseButton.IsEnabled = false;
        OutputPreviewFullscreenButton.IsEnabled = false;
        OutputPreviewClockText.Text = "00:00.000 / 00:00.000";
        OutputPreviewFrameText.Text = "output 0/0";
        OutputFullscreenClockText.Text = "00:00.000 / 00:00.000";
        OutputFullscreenFrameText.Text = "0 / 0";
    }

    private void RefreshCurrentOutputVideoFrame()
    {
        if (outputVideoFrameCount > 0 && outputVideoPlan.Count > 0)
            ShowOutputVideoFrame(outputVideoFrameIndex);
    }

    private void Desay97ProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (session is not null && Desay97ProfileComboBox.SelectedIndex >= 0)
            ConfigurationHintText.Text = $"Palm profile selected · {ActiveDecodeContext()} · close the menu to decode.";
    }

    private async void EventVersionComboBox_OnDropDownClosed(object? sender, EventArgs e)
    {
        var isDesay97 = EventVersionComboBox.SelectedItem is ComboBoxItem versionItem &&
                        versionItem.Content?.ToString() == "0x97";
        if (session is not null && !isDesay97 && EventVersionComboBox.SelectedIndex >= 0 &&
            !SelectedDecodeConfigurationMatchesActive())
            await DecodeSelectedAsync();
    }

    private async void Desay97ProfileComboBox_OnDropDownClosed(object? sender, EventArgs e)
    {
        var isDesay97 = EventVersionComboBox.SelectedItem is ComboBoxItem versionItem &&
                        versionItem.Content?.ToString() == "0x97";
        if (session is not null && isDesay97 && Desay97ProfileComboBox.SelectedIndex >= 0 &&
            NvtRegisterCatalog.FindProfile(session.RegisterProfile) is not null &&
            !SelectedDecodeConfigurationMatchesActive())
            await DecodeSelectedAsync();
        else if (session is not null && isDesay97 && Desay97ProfileComboBox.SelectedIndex >= 0)
            ConfigurationHintText.Text = $"Palm profile confirmed · {ActiveDecodeContext()} · select the IC profile to decode 0x97.";
    }

    private bool SelectedDecodeConfigurationMatchesActive()
    {
        if (session is null || decodeConfiguration is null ||
            EventVersionComboBox.SelectedItem is not ComboBoxItem versionItem)
            return false;

        var version = versionItem.Content?.ToString() ?? string.Empty;
        var palmProfile = version.Equals("0x97", StringComparison.OrdinalIgnoreCase)
            ? Desay97ProfileComboBox.SelectedItem is ComboBoxItem palmItem
                ? palmItem.Content?.ToString()
                : null
            : null;
        var registerProfile = NvtRegisterCatalog.FindProfile(session.RegisterProfile);
        var eventBufferBase = registerProfile?.EventBufferBase ?? 0;
        return decodeConfiguration.EventBufferVersion.Equals(version, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(decodeConfiguration.Desay97Profile, palmProfile, StringComparison.OrdinalIgnoreCase) &&
               decodeConfiguration.SourceAdapterId.Equals(session.Probe.AdapterId, StringComparison.Ordinal) &&
               decodeConfiguration.EventBufferBase == eventBufferBase &&
               string.Equals(decodeConfiguration.RegisterProfile, session.RegisterProfile, StringComparison.OrdinalIgnoreCase);
    }

    private string ActiveDecodeContext()
    {
        if (decodeConfiguration is null) return "no active replay";
        var format = decodeConfiguration.EventBufferVersion.Equals("0x97", StringComparison.OrdinalIgnoreCase)
            ? $"Desay 0x97 / {decodeConfiguration.Desay97Profile ?? "unconfirmed Palm"}"
            : $"Common {decodeConfiguration.EventBufferVersion}";
        return $"active replay: {format}";
    }

    private async void RegisterProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringRegisterProfile || session is null ||
            RegisterProfileComboBox.SelectedItem is not RegisterProfileChoice choice)
            return;

        await ApplyRegisterProfileAsync(choice);
    }

    private async Task ApplyRegisterProfileAsync(RegisterProfileChoice choice)
    {
        if (session is null) return;
        var selectedId = (RawRecordsList.SelectedItem as RawRecordRow)?.Record.StableId;
        var loadedSession = session;
        SetBusy(true, choice.IcFamily is null ? "Removing IC register profile" : $"Applying IC profile {choice.IcFamily}");
        try
        {
            session = await Task.Run(() => loadedSession.WithRegisterProfile(choice.IcFamily));
            var projected = await Task.Run(() => RegisterActivityProjector.Project(session.Records).ToArray());
            ApplyRawExplorerProjection(projected, selectedId);
            diagnosticRows = session.SourceDiagnostics.Select(diagnostic => new DiagnosticRow(diagnostic)).ToArray();
            CreateReviewSession(session.SourceDiagnostics);
            var ambiguous = registerActivities.Count(item => item.IsAmbiguous);
            ConfigurationHintText.Text = choice.IcFamily is null
                ? $"IC profile unconfirmed · {ambiguous:N0} collision-prone register events remain raw-only"
                : $"IC profile {choice.IcFamily} confirmed · register semantics and Event Buffer base reapplied";
            SessionStatusText.Text = choice.IcFamily is null
                ? "Register profile removed; original capture remains unchanged"
                : $"Register profile {choice.IcFamily} applied without changing source bytes";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            SessionStatusText.Text = $"Register profile failed · {exception.Message}";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }

        var canDecode = EventVersionComboBox.SelectedIndex >= 0 &&
            (EventVersionComboBox.SelectedIndex != 4 ||
             (choice.IcFamily is not null && Desay97ProfileComboBox.SelectedIndex >= 0));
        if (canDecode) await DecodeSelectedAsync();
    }

    private void RegisterFilterComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshRawExplorer();

    private void RegisterSearchTextBox_OnTextChanged(object? sender, TextChangedEventArgs e) => RefreshRawExplorer();

    private void ApplyRawExplorerProjection(RegisterActivityEntry[] projected, string? preferredStableId = null)
    {
        registerActivities = projected;
        var activityById = projected.ToDictionary(item => item.Record.StableId, StringComparer.Ordinal);
        allRawRows = session?.Records
            .Select(record => new RawRecordRow(record, activityById.GetValueOrDefault(record.StableId)))
            .ToArray() ?? [];
        rawRowsById = allRawRows.ToDictionary(row => row.Record.StableId, StringComparer.Ordinal);
        RefreshRawExplorer(preferredStableId);
    }

    private void RefreshRawExplorer(string? preferredStableId = null)
    {
        if (RawRecordsList is null || RegisterActivitySurface is null) return;
        preferredStableId ??= (RawRecordsList.SelectedItem as RawRecordRow)?.Record.StableId;
        var filter = (RegisterFilterComboBox.SelectedItem as RawRegisterFilterChoice)?.Filter ?? RawRegisterFilter.All;
        var query = RegisterSearchTextBox.Text ?? string.Empty;
        var visible = allRawRows.Where(row => row.Matches(query) && filter switch
        {
            RawRegisterFilter.All => true,
            RawRegisterFilter.Registers => row.Activity is not null,
            RawRegisterFilter.ChangedReads => row.Activity?.ChangedFromPreviousSample == true,
            RawRegisterFilter.WritesAndCommands => row.Activity?.Kind is RegisterActivityKind.Write or RegisterActivityKind.Command or RegisterActivityKind.Reset,
            RawRegisterFilter.Ambiguous => row.Activity?.IsAmbiguous == true,
            _ => true,
        }).ToArray();
        RawRecordsList.ItemsSource = visible;
        var visibleIds = visible.Select(row => row.Record.StableId).ToHashSet(StringComparer.Ordinal);
        var visibleActivities = registerActivities.Where(item => visibleIds.Contains(item.Record.StableId)).ToArray();
        var minimum = allRawRows.Length == 0 ? 0 : allRawRows.Min(row => row.Record.Index);
        var maximum = allRawRows.Length == 0 ? 1 : allRawRows.Max(row => row.Record.Index);
        RegisterActivitySurface.SetActivities(visibleActivities, minimum, maximum);
        RegisterActivitySurface.IsEnabled = visibleActivities.Length > 0;
        RegisterResultText.Text = $"{visible.Length:N0} records · {visibleActivities.Length:N0} register events" +
            (visibleActivities.Any(item => item.IsAmbiguous) ? $" · {visibleActivities.Count(item => item.IsAmbiguous):N0} ambiguous" : string.Empty);
        if (preferredStableId is not null && visible.FirstOrDefault(row => row.Record.StableId == preferredStableId) is { } preferred)
            RawRecordsList.SelectedItem = preferred;
        else if (visible.Length > 0)
            RawRecordsList.SelectedItem = visible[0];
        else
        {
            RawRecordsList.SelectedItem = null;
            RegisterActivitySurface.SetSelected(null);
            InspectorTitleText.Text = "No matching record";
            InspectorSubtitleText.Text = "Adjust the Raw Explorer search or register filter.";
            InspectorLogicalText.Text = "-";
            InspectorCrcText.Text = "-";
            InspectorAsilText.Text = "-";
            InspectorAllBreakText.Text = "ALL BREAK";
            InspectorAllBreakBadge.IsVisible = false;
            ProtocolFieldsItemsControl.ItemsSource = null;
            TransportFieldsItemsControl.ItemsSource = null;
        }
    }

    private void RegisterActivitySurface_OnActivitySelected(object? sender, RegisterActivitySelectedEventArgs e)
    {
        if (!rawRowsById.TryGetValue(e.Record.StableId, out var row)) return;
        RawRecordsList.SelectedItem = row;
        RawRecordsList.ScrollIntoView(row);
    }

    private async void ExportReadableLogButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export readable communication log",
            AllowMultiple = false,
        });
        var directory = folders.SingleOrDefault()?.TryGetLocalPath();
        if (directory is null) return;

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, "Exporting readable communication log");
        try
        {
            var result = await new ReadableCommunicationLogWriter().WriteAsync(directory, session.Records, cancellationToken);
            SessionStatusText.Text =
                $"Readable log exported · {result.RecordCount:N0} records · {result.AnnotatedRegisterCount:N0} annotated · {result.AmbiguousRegisterCount:N0} ambiguous";
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Readable log export cancelled; prior complete output was preserved";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SessionStatusText.Text = $"Readable log export failed · {exception.Message}";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private void RawRecordsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RawRecordsList.SelectedItem is RawRecordRow row)
        {
            RegisterActivitySurface.SetSelected(row.Record.StableId);
            ShowRecord(row.Record, FindFrame(row.Record.StableId));
            if (!synchronizingSelection && replaySession is not null)
            {
                var logicalIndex = Array.FindIndex(decodedRows, item =>
                    item.PhysicalRecords.Any(source => source.StableId == row.Record.StableId));
                if (logicalIndex >= 0)
                {
                    SeekReplay(logicalIndex);
                }
            }
        }
    }

    private void DecodedFramesList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DecodedFramesList.SelectedItem is not DecodedFrameRow row)
        {
            return;
        }

        if (!synchronizingSelection && replaySession is not null)
        {
            SeekReplay(row.LogicalIndex);
            return;
        }

        var physical = row.PhysicalRecords[^1];
        ShowRecord(physical, row.Frame);
        if (rawRowsById.TryGetValue(physical.StableId, out var rawRow))
        {
            RawRecordsList.SelectedItem = rawRow;
            RawRecordsList.ScrollIntoView(rawRow);
        }
    }

    private void DiagnosticListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DiagnosticListBox.SelectedItem is not ReviewGroupRow row)
        {
            return;
        }
        selectedReviewGroupId = row.Group.Id;
        var details = row.Group.Occurrences.FirstOrDefault()?.Diagnostic.Details;
        selectedMarkerId = details?.GetValueOrDefault("marker_id");
        AnnotationMetadataPanel.IsVisible = selectedMarkerId is not null;
        MarkerNameTextBox.Text = selectedMarkerId is null
            ? string.Empty
            : markers.FirstOrDefault(marker => marker.Id == selectedMarkerId)?.Label ?? string.Empty;
        MarkerQaCaseTextBox.Text = selectedMarkerId is null
            ? string.Empty
            : string.Join(", ", markers.FirstOrDefault(marker => marker.Id == selectedMarkerId)?.QaCaseIds ?? []);
        ReviewActionsPanel.IsVisible = true;
        ReviewEmptyText.IsVisible = false;
        ReviewOccurrenceComboBox.ItemsSource = row.Group.Occurrences
            .Select((occurrence, index) => new ReviewOccurrenceRow(index + 1, occurrence))
            .ToArray();
        ComboBoxAutoSizer.Fit(ReviewOccurrenceComboBox);
        ReviewOccurrenceComboBox.SelectedIndex = 0;
        UpdateReviewState(row.Group);
    }

    private void ReviewGroupBorder_OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.DataContext is not ReviewGroupRow { CanUnmark: true } row)
        {
            CloseContextMenu(control);
            return;
        }
        DiagnosticListBox.SelectedItem = row;
        var matchingMarkers = markers.Where(marker => marker.Id == row.MarkerId).ToArray();
        if (matchingMarkers.Length == 0)
        {
            CloseContextMenu(control);
            return;
        }
        OpenMarkerContextMenu(control, matchingMarkers);
        e.Handled = true;
    }

    private void ReplayTimelineSurface_OnMarkerContextRequested(object? sender, ReplayTimelineContextEventArgs e)
    {
        var matchingMarkers = markers
            .Where(marker => marker.StartLogicalIndex <= e.LogicalIndex && marker.EndLogicalIndex >= e.LogicalIndex)
            .OrderBy(marker => marker.StartLogicalIndex)
            .ThenBy(marker => marker.CreatedAt)
            .ToArray();
        if (matchingMarkers.Length == 0)
        {
            CloseContextMenu(ReplayTimelineSurface);
            return;
        }
        OpenMarkerContextMenu(ReplayTimelineSurface, matchingMarkers);
        e.Handled = true;
    }

    private void OpenMarkerContextMenu(Control target, IEnumerable<ReplayMarker> matchingMarkers)
    {
        var items = matchingMarkers.Select(marker =>
        {
            var range = marker.IsRange
                ? $"frames {marker.StartLogicalIndex + 1:N0}-{marker.EndLogicalIndex + 1:N0}"
                : $"frame {marker.StartLogicalIndex + 1:N0}";
            var item = new MenuItem { Header = $"Unmark {marker.Label} · {range}", Tag = marker.Id };
            item.Click += (_, _) =>
            {
                CloseContextMenu(target);
                RemoveMarker(marker.Id);
            };
            return item;
        }).ToArray();
        if (items.Length == 0) return;
        CloseContextMenu(target);
        var menu = new ContextMenu { ItemsSource = items };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(target.ContextMenu, menu)) target.ContextMenu = null;
        };
        target.ContextMenu = menu;
        menu.Open(target);
    }

    private static void CloseContextMenu(Control target)
    {
        var menu = target.ContextMenu;
        if (menu is not null) menu.Close();
        if (ReferenceEquals(target.ContextMenu, menu)) target.ContextMenu = null;
    }

    private void ReviewOccurrenceComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ReviewOccurrenceComboBox.SelectedItem is ReviewOccurrenceRow row)
            NavigateToOccurrence(row.Occurrence);
    }

    private void NavigateToOccurrence(ReviewOccurrence occurrence)
    {
        if (occurrence.LogicalIndex is { } logicalIndex && replaySession is { Count: > 0 })
            SeekReplay(logicalIndex);
        else if (rawRowsById.TryGetValue(occurrence.Diagnostic.SourceRecordId, out var rawRow))
        {
            ShowRecord(rawRow.Record, FindFrame(rawRow.Record.StableId));
            RawRecordsList.SelectedItem = rawRow;
            RawRecordsList.ScrollIntoView(rawRow);
        }
        InspectorSubtitleText.Text = $"{occurrence.Diagnostic.Severity} · {occurrence.Diagnostic.Code} · {occurrence.Diagnostic.Message}";
    }

    private void ReviewFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } tag } && Enum.TryParse<ReviewQueueFilter>(tag.ToString(), out var filter))
        {
            reviewFilter = filter;
            RefreshReviewQueue();
        }
    }

    private void SettingsPage_OnPlaybackPreferencesChanged(object? sender, ReplayPlaybackPreferences preferences)
    {
        ApplyPlaybackPreferences(preferences, syncTransportControls: true);
    }

    private void AutoPauseSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        TransportAutoPausePanel.IsVisible = !TransportAutoPausePanel.IsVisible;

    private void OpenPlaybackSettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TransportAutoPausePanel.IsVisible = false;
        OpenSettingsPage();
        SettingsPage.NavigateToPlayback();
    }

    private void TransportAutoPauseOption_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingAutoPauseControls || TransportPauseOnAlarmCheckBox is null) return;
        var preferences = new ReplayPlaybackPreferences(
            TransportPauseOnAlarmCheckBox.IsChecked == true,
            TransportPauseOnBreakCheckBox.IsChecked == true,
            TransportPauseOnAllBreakCheckBox.IsChecked == true);
        SettingsPage.SetPlaybackPreferences(preferences);
        ApplyPlaybackPreferences(preferences, syncTransportControls: false);
    }

    private void ApplyPlaybackPreferences(ReplayPlaybackPreferences preferences, bool syncTransportControls)
    {
        if (syncTransportControls)
        {
            synchronizingAutoPauseControls = true;
            try
            {
                TransportPauseOnAlarmCheckBox.IsChecked = preferences.PauseOnAlarmOrQaFail;
                TransportPauseOnBreakCheckBox.IsChecked = preferences.PauseOnBreak;
                TransportPauseOnAllBreakCheckBox.IsChecked = preferences.PauseOnAllBreak;
            }
            finally
            {
                synchronizingAutoPauseControls = false;
            }
        }
        UpdateAutoPauseIndicator(preferences);
        if (reviewSession is not null)
        {
            var enabled = preferences.PauseOnAlarmOrQaFail;
            reviewSession.Options = reviewSession.Options with { PauseOnAlarm = enabled, PauseOnQaFail = enabled };
        }
    }

    private void AcknowledgeReviewButton_OnClick(object? sender, RoutedEventArgs e) =>
        MutateReview(groupId => reviewSession!.Acknowledge(groupId));

    private void DispositionReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } tag } && Enum.TryParse<ReviewDisposition>(tag.ToString(), out var disposition))
            MutateReview(groupId => reviewSession!.SetDisposition(groupId, disposition));
    }

    private void ResolveReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            MutateReview(groupId => reviewSession!.Resolve(groupId));
        }
        catch (InvalidOperationException exception)
        {
            ReviewStateText.Text = $"Cannot resolve · {exception.Message}";
        }
    }

    private void MutateReview(Func<string, ReviewEventGroup> action)
    {
        if (reviewSession is null || selectedReviewGroupId is not { } groupId) return;
        var updated = action(groupId);
        RefreshReviewQueue(groupId);
        UpdateReviewState(updated);
    }

    private void UpdateReviewState(ReviewEventGroup group)
    {
        var details = group.Occurrences.FirstOrDefault()?.Diagnostic.Details;
        var qa = details?.GetValueOrDefault("qa_case_ids");
        var evidence = details?.GetValueOrDefault("evidence");
        ReviewStateText.Text =
            $"captured={group.CurrentCapturedAlarmState} · workflow={group.WorkflowState} · " +
            $"lifecycle={group.AsilLifecycle?.ToString() ?? "-"} · disposition={group.Disposition}" +
            (string.IsNullOrWhiteSpace(qa) ? string.Empty : $"\nQA={qa}") +
            (string.IsNullOrWhiteSpace(evidence) || evidence == "-" ? string.Empty : $"\nevidence={evidence}");
    }

    private void CreateReviewSession(IReadOnlyList<ReplayDiagnostic> diagnostics)
    {
        var previousState = reviewSession?.ExportState() ?? [];
        baseDiagnostics = diagnostics;
        diagnosticLineNumbers = diagnosticRows
            .Select(row => row.Diagnostic.Location.LineNumber)
            .OrderBy(line => line)
            .ToArray();
        var logicalIndices = decodedRows
            .SelectMany(row => row.PhysicalRecords.Append(row.Source).Select(source => (source.StableId, row.LogicalIndex)))
            .GroupBy(item => item.StableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().LogicalIndex, StringComparer.Ordinal);
        reviewSession = new ReviewSession(
            diagnostics.Concat(markers.Select(MarkerDiagnostic)).ToArray(),
            logicalIndices,
            new ReviewSessionOptions(
                SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail,
                SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail));
        reviewSession.ImportState(previousState);
        RefreshReviewQueue();
    }

    private ReplayDiagnostic MarkerDiagnostic(ReplayMarker marker)
    {
        var index = Math.Clamp(marker.StartLogicalIndex, 0, Math.Max(0, decodedRows.Length - 1));
        var source = decodedRows.Length > 0 ? decodedRows[index].Source : session?.Records.FirstOrDefault();
        var evidence = marker.Evidence ?? [];
        return new ReplayDiagnostic(
            DiagnosticSeverity.Info,
            "ANNOTATION_MARKER",
            marker.IsRange ? $"{marker.Label} · frames {marker.StartLogicalIndex + 1}-{marker.EndLogicalIndex + 1}" : $"{marker.Label} · frame {marker.StartLogicalIndex + 1}",
            source?.StableId ?? session?.SourceSha256 ?? "sidecar",
            source?.Location ?? new SourceLocation(0, 0),
            new Dictionary<string, string>
            {
                ["marker_id"] = marker.Id,
                ["qa_case_ids"] = string.Join(", ", marker.QaCaseIds ?? []),
                ["evidence"] = evidence.Count == 0 ? "-" : string.Join("; ", evidence.Select(item => $"{item.Kind}:{item.Label ?? Path.GetFileName(item.Path)}:{(File.Exists(item.Path) ? "available" : "missing")}")),
            });
    }

    private void RefreshReviewQueue(string? selectGroupId = null)
    {
        reviewRows = reviewSession?.Filter(reviewFilter)
            .Where(group => group.Severity >= DiagnosticSeverity.Warning || group.IsAsil ||
                            group.Category is ReviewEventCategory.QaResult or ReviewEventCategory.Annotation)
            .Select(group => new ReviewGroupRow(group)).ToArray() ?? [];
        DiagnosticListBox.ItemsSource = reviewRows;
        DiagnosticCountText.Text = reviewRows.Length.ToString(CultureInfo.InvariantCulture);
        ClearMarkersButton.IsEnabled = markers.Count > 0;
        ReplayTimelineSurface.SetMarkerFrames(markers.Select(marker => marker.StartLogicalIndex));
        var selected = selectGroupId is null ? null : reviewRows.FirstOrDefault(row => row.Group.Id == selectGroupId);
        if (selected is not null) DiagnosticListBox.SelectedItem = selected;
        if (Bounds.Width > 0) ApplyResponsiveRails(Bounds.Width);
    }

    private void AddMarkerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0) return;
        var marker = new ReplayMarker(
            $"marker-{Guid.NewGuid():N}",
            NextMarkerLabel(),
            currentLogicalIndex,
            currentLogicalIndex,
            DateTimeOffset.UtcNow);
        markers.Add(marker);
        CreateReviewSession(baseDiagnostics);
        RefreshReviewQueue($"ANNOTATION_MARKER:{marker.Id}");
        SessionStatusText.Text = $"Added marker · frame {currentLogicalIndex + 1}";
    }

    private string NextMarkerLabel()
    {
        var number = 1;
        while (markers.Any(marker => marker.Label.Equals($"Marker {number}", StringComparison.OrdinalIgnoreCase)))
            number++;
        return $"Marker {number}";
    }

    private void UnmarkButton_OnClick(object? sender, RoutedEventArgs e) => RemoveMarker(selectedMarkerId);

    private void RemoveMarker(string? markerId)
    {
        if (markerId is null) return;
        var index = markers.FindIndex(marker => marker.Id == markerId);
        if (index < 0) return;
        var removed = markers[index];
        markers.RemoveAt(index);
        selectedMarkerId = null;
        selectedReviewGroupId = null;
        CloseContextMenu(ReplayTimelineSurface);
        CreateReviewSession(baseDiagnostics);
        DiagnosticListBox.SelectedItem = null;
        ReviewActionsPanel.IsVisible = false;
        ReviewEmptyText.IsVisible = true;
        AnnotationMetadataPanel.IsVisible = false;
        MarkerNameTextBox.Text = string.Empty;
        MarkerQaCaseTextBox.Text = string.Empty;
        if (replaySession is { Count: > 0 } && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
        RefreshOutputPreviewIfVisible();
        SessionStatusText.Text = removed.IsRange
            ? $"Removed range marker · frames {removed.StartLogicalIndex + 1}-{removed.EndLogicalIndex + 1}"
            : $"Removed marker · frame {removed.StartLogicalIndex + 1}";
    }

    private void RenameMarkerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMarker(out var marker, out var index)) return;
        var label = (MarkerNameTextBox.Text ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            SessionStatusText.Text = "Marker name cannot be empty";
            MarkerNameTextBox.Text = marker.Label;
            return;
        }
        if (markers.Any(item => item.Id != marker.Id && item.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
        {
            SessionStatusText.Text = $"Marker name already exists · {label}";
            MarkerNameTextBox.Text = marker.Label;
            return;
        }

        markers[index] = marker with { Label = label };
        RefreshMarkerReview(marker.Id, $"Renamed marker · {label}");
    }

    private void ClearMarkersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (markers.Count == 0) return;
        var count = markers.Count;
        markers.Clear();
        ReplayTimelineSurface.SetMarkerFrames([]);
        selectedMarkerId = null;
        selectedReviewGroupId = null;
        CloseContextMenu(ReplayTimelineSurface);
        CreateReviewSession(baseDiagnostics);
        DiagnosticListBox.SelectedItem = null;
        ReviewActionsPanel.IsVisible = false;
        ReviewEmptyText.IsVisible = true;
        AnnotationMetadataPanel.IsVisible = false;
        MarkerNameTextBox.Text = string.Empty;
        MarkerQaCaseTextBox.Text = string.Empty;
        RefreshReviewQueue();
        if (replaySession is { Count: > 0 } && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
        RefreshOutputPreviewIfVisible();
        SessionStatusText.Text = $"Cleared {count} marker{(count == 1 ? string.Empty : "s")}";
    }

    private void ApplyMarkerQaButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMarker(out var marker, out var index)) return;
        var qaCaseIds = (MarkerQaCaseTextBox.Text ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        markers[index] = marker with { QaCaseIds = qaCaseIds };
        RefreshMarkerReview(marker.Id, $"QA references updated · {qaCaseIds.Length} IDs");
    }

    private async void AttachMarkerEvidenceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMarker(out var marker, out var index)) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach raw Kernel / FW log",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Raw log evidence") { Patterns = ["*.log", "*.txt", "*.dmesg", "*.trace"] }, FilePickerFileTypes.All],
        });
        var path = files.SingleOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            var name = Path.GetFileName(path);
            var lowerName = name.ToLowerInvariant();
            var kind = lowerName.Contains("kernel") || lowerName.Contains("dmesg")
                ? ReplayEvidenceKind.KernelLog
                : lowerName.Contains("fw") || lowerName.Contains("firmware")
                    ? ReplayEvidenceKind.FirmwareLog
                    : ReplayEvidenceKind.Other;
            var evidence = (marker.Evidence ?? []).Append(new ReplayEvidenceReference(
                $"evidence-{Guid.NewGuid():N}", kind, path, hash, name)).ToArray();
            markers[index] = marker with { Evidence = evidence };
            RefreshMarkerReview(marker.Id, $"Raw evidence attached · {name} · SHA-256 recorded");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SessionStatusText.Text = $"Evidence attach failed · {exception.Message}";
        }
    }

    private bool TryGetSelectedMarker(out ReplayMarker marker, out int index)
    {
        index = selectedMarkerId is null ? -1 : markers.FindIndex(item => item.Id == selectedMarkerId);
        marker = index >= 0 ? markers[index] : null!;
        return index >= 0;
    }

    private void RefreshMarkerReview(string markerId, string status)
    {
        CreateReviewSession(baseDiagnostics);
        RefreshReviewQueue($"ANNOTATION_MARKER:{markerId}");
        SessionStatusText.Text = status;
    }

    private async void SaveReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || decodeConfiguration is null || reviewSession is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save replay review",
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.SourcePath) + ".nvtreplay.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("NVT replay review") { Patterns = ["*.nvtreplay.json"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var document = new ReplaySidecarDocument(
                ReplaySidecarDocument.CurrentSchemaVersion,
                session.SourceSha256,
                Path.GetFileName(session.SourcePath),
                decodeConfiguration,
                markers.ToArray(),
                reviewSession.ExportState(),
                new Dictionary<string, bool>
                {
                    ["pauseOnAlarm"] = SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail,
                    ["pauseOnBreak"] = SettingsPage.PlaybackPreferences.PauseOnBreak,
                    ["pauseOnAllBreak"] = SettingsPage.PlaybackPreferences.PauseOnAllBreak,
                    ["compressIdle"] = CompressIdleCheckBox.IsChecked == true,
                },
                DateTimeOffset.UtcNow);
            await new ReplaySidecarStore().SaveAsync(path, document);
            SessionStatusText.Text = $"Review saved atomically · {Path.GetFileName(path)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SessionStatusText.Text = $"Review save failed · {exception.Message}";
        }
    }

    private async void LoadReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || decodeConfiguration is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open replay review",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("NVT replay review") { Patterns = ["*.nvtreplay.json"] }],
        });
        var path = files.SingleOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var loaded = await new ReplaySidecarStore().LoadAsync(path, session.SourceSha256, decodeConfiguration);
            if (loaded.RequiresExplicitConfirmation)
            {
                pendingSidecar = loaded;
                ApplySidecarButton.IsVisible = true;
                LoadReviewButton.IsVisible = false;
                SessionStatusText.Text = $"Review mismatch not applied · {string.Join(" ", loaded.Warnings)}";
                return;
            }
            ApplySidecar(loaded);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or UnsupportedReplaySidecarVersionException)
        {
            SessionStatusText.Text = $"Review open failed · {exception.Message}";
        }
    }

    private void ApplySidecarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (pendingSidecar is { } loaded) ApplySidecar(loaded);
    }

    private void ApplySidecar(ReplaySidecarOpenResult loaded)
    {
        var resolvedById = loaded.Evidence.ToDictionary(item => item.Reference.Id, item => item.ResolvedPath, StringComparer.Ordinal);
        markers.Clear();
        markers.AddRange(loaded.Document.Markers.Select(marker => marker with
        {
            Evidence = marker.Evidence?.Select(reference => reference with
            {
                Path = resolvedById.GetValueOrDefault(reference.Id, reference.Path),
            }).ToArray(),
        }));
        var currentPreferences = SettingsPage.PlaybackPreferences;
        SettingsPage.SetPlaybackPreferences(new ReplayPlaybackPreferences(
            loaded.Document.VisibilityPreferences.TryGetValue("pauseOnAlarm", out var pauseOnAlarm)
                ? pauseOnAlarm
                : currentPreferences.PauseOnAlarmOrQaFail,
            loaded.Document.VisibilityPreferences.GetValueOrDefault("pauseOnBreak"),
            loaded.Document.VisibilityPreferences.GetValueOrDefault("pauseOnAllBreak")));
        if (loaded.Document.VisibilityPreferences.TryGetValue("compressIdle", out var compress)) CompressIdleCheckBox.IsChecked = compress;
        CreateReviewSession(baseDiagnostics);
        var unresolved = reviewSession?.ImportState(loaded.Document.ReviewStates) ?? [];
        RefreshReviewQueue();
        pendingSidecar = null;
        ApplySidecarButton.IsVisible = false;
        LoadReviewButton.IsVisible = true;
        SessionStatusText.Text =
            $"Review opened · {markers.Count} markers · {loaded.Evidence.Count} evidence refs" +
            (loaded.Warnings.Count > 0 ? $" · {loaded.Warnings.Count} warnings" : string.Empty) +
            (unresolved.Count > 0 ? $" · {unresolved.Count} stale review groups" : string.Empty);
    }

    private object? FindFrame(string sourceId) =>
        decodedRowsBySourceId.GetValueOrDefault(sourceId)?.Frame;

    private void ShowRecord(SourceRecord record, object? frame, ITouchReplaySnapshot? snapshot = null)
    {
        var registerReadable = record.SourceFields?.GetValueOrDefault("register_readable");
        currentInspectorRecord = record;
        currentInspectorFrame = frame;
        currentInspectorSnapshot = snapshot;
        currentInspectorPresentation = InspectorPresentationBuilder.Build(
            record,
            frame,
            snapshot,
            replaySession?.Count ?? 0,
            PaintSurface.Mode);
        if (snapshot is not null)
            currentInspectorPresentation = currentInspectorPresentation with
            {
                TimestampLabel = FormatClock(SelectedTime(snapshot.Timeline)),
            };
        ApplyInspectorPresentation(currentInspectorPresentation, frame is null && registerReadable is not null);

        var frameAlerts = reviewSession?.FrameAlerts(record.StableId, record.Location.LineNumber) ?? [];
        var crcFailed = currentInspectorPresentation.CrcFailed;
        var asilAlarm = currentInspectorPresentation.AsilAlarm;
        InspectorAlertBorder.IsVisible = frameAlerts.Count > 0 || crcFailed || asilAlarm;
        InspectorAlertText.Text = frameAlerts.Count > 0
            ? string.Join(" · ", frameAlerts.Select(item => item.Code))
            : crcFailed ? "CRC validation failed" : asilAlarm ? "ASIL alarm active" : string.Empty;
        var hasNavigableFindings = reviewSession?.HasNavigableFindings == true;
        PreviousFindingButton.IsVisible = hasNavigableFindings;
        NextFindingButton.IsVisible = hasNavigableFindings;
        SourceLineText.Text = record.Location.LineNumber.ToString(CultureInfo.InvariantCulture);
        SourceOffsetText.Text = record.Location.ByteOffset.ToString(CultureInfo.InvariantCulture);
        StableIdText.Text = record.StableId;
        TransportFieldsItemsControl.ItemsSource = BuildTransportRows(record);
        SourceFieldsText.Text = FormatSourceFields(record);
        var rawLayout = RawFrameLayoutBuilder.Build(record, frame);
        RawLayoutTitleText.Text = rawLayout.Title;
        RawLayoutSummaryText.Text = rawLayout.Summary;
        RawByteSectionsItemsControl.ItemsSource = rawLayout.Sections;
        RawBytesText.Text = FormatBytes(record.Data);
        ProtocolFieldsItemsControl.ItemsSource = currentInspectorPresentation.ProtocolRows;
    }

    private void ApplyInspectorPresentation(InspectorFramePresentation presentation, bool registerActivity)
    {
        var selectedId = (InspectorContactsList.SelectedItem as InspectorContactRow)?.Id;
        InspectorTitleText.Text = registerActivity ? "Register" : "Frame";
        InspectorLogicalText.Text = presentation.LogicalLabel;
        InspectorTimestampText.Text = presentation.TimestampLabel;
        InspectorSubtitleText.Text = presentation.Subtitle;
        InspectorFingerText.Text = presentation.ContactSummary.Fingers.ToString(CultureInfo.InvariantCulture);
        InspectorGloveText.Text = presentation.ContactSummary.Gloves.ToString(CultureInfo.InvariantCulture);
        InspectorPalmText.Text = presentation.ContactSummary.Palms.ToString(CultureInfo.InvariantCulture);
        InspectorContactCountText.Text = presentation.ContactSummary.Total == 1
            ? "1 active"
            : $"{presentation.ContactSummary.Total} active";
        InspectorNoContactsText.IsVisible = false;
        InspectorContactsList.IsVisible = presentation.Contacts.Count > 0;
        InspectorContactsList.ItemsSource = presentation.Contacts;
        InspectorContactsList.SelectedItem = selectedId is { } id
            ? presentation.Contacts.FirstOrDefault(contact => contact.Id == id)
            : null;
        PaintSurface.SetHighlightedContact((InspectorContactsList.SelectedItem as InspectorContactRow)?.Id);

        InspectorCrcText.Text = presentation.CrcLabel;
        InspectorAsilText.Text = presentation.AsilLabel;
        InspectorAsilRawText.Text = presentation.AsilRawLabel is "-" ? string.Empty : presentation.AsilRawLabel;
        SetHealthText(InspectorCrcText, presentation.CrcFailed, presentation.CrcLabel != "-");
        SetHealthText(InspectorAsilText, presentation.AsilAlarm, presentation.AsilLabel != "-");
        InspectorAllBreakBadge.IsVisible = presentation.AllBreak;
        InspectorAllBreakText.Text = "ALL BREAK";

        CollapsedFingerText.Text = presentation.ContactSummary.Fingers.ToString(CultureInfo.InvariantCulture);
        CollapsedGloveText.Text = presentation.ContactSummary.Gloves.ToString(CultureInfo.InvariantCulture);
        CollapsedPalmText.Text = presentation.ContactSummary.Palms.ToString(CultureInfo.InvariantCulture);
        CollapsedAsilText.Text = presentation.AsilLabel switch
        {
            "NORMAL" => "ASIL\nNORM",
            "ALARM" => "ASIL\nALRM",
            _ => "ASIL -",
        };
        ToolTip.SetTip(CollapsedAsilBadge, presentation.AsilLabel == "-"
            ? "ASIL unavailable"
            : $"ASIL {presentation.AsilLabel} {presentation.AsilRawLabel}");
        SetHealthState(CollapsedAsilBadge, presentation.AsilAlarm, presentation.AsilLabel != "-");
    }

    private static void SetHealthState(Border border, bool alarm, bool known)
    {
        border.Classes.Set("healthGood", known && !alarm);
        border.Classes.Set("healthAlarm", known && alarm);
    }

    private static void SetHealthText(TextBlock textBlock, bool alarm, bool known)
    {
        textBlock.Classes.Set("healthTextAlarm", known && alarm);
        textBlock.Classes.Set("healthTextNormal", !alarm || !known);
    }

    private async void SourceAdapterComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringSourceChoice || pendingSourcePath is not { } path ||
            SourceAdapterComboBox.SelectedItem is not SourceAdapterChoice choice)
            return;
        await OpenCaptureAsync(path, choice.AdapterId);
    }

    private static IReadOnlyList<InspectorDetailRow> BuildTransportRows(SourceRecord record)
    {
        var rows = new List<InspectorDetailRow>
        {
            new("Operation", $"{record.Operation} {record.Target}"),
            new("Register", record.Address is { } address ? $"0x{address:X}" : "Not reported"),
            new("Payload", record.DeclaredByteCount is { } declared
                ? $"{record.Data.Count} bytes, declared {declared}"
                : $"{record.Data.Count} bytes"),
        };
        if (record.I2c is not { } i2c) return rows;
        rows.Add(new InspectorDetailRow("Slave", $"0x{i2c.SlaveAddress:X2}"));
        rows.Add(new InspectorDetailRow("Address ACK", i2c.AddressAcknowledged switch
        {
            true => "ACK",
            false => "NAK",
            null => "Not reported",
        }));
        rows.Add(new InspectorDetailRow("Write", i2c.WriteCommands.Count == 0
            ? "None"
            : string.Join(" | ", i2c.WriteCommands.Select(FormatBytes))));
        rows.Add(new InspectorDetailRow("Data ACK", I2cAckSummary.Format(i2c.Acked)));
        if (!string.IsNullOrWhiteSpace(i2c.Error)) rows.Add(new InspectorDetailRow("Transport error", i2c.Error));
        return rows;
    }

    private static string FormatSourceFields(SourceRecord record) =>
        record.SourceFields is not { Count: > 0 }
            ? "No adapter-specific source fields."
            : string.Join('\n', record.SourceFields
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(field => $"{field.Key}={field.Value}"));

    private sealed record SourceAdapterChoice(string AdapterId, string DisplayName, ProbeConfidence Confidence)
    {
        public override string ToString() => $"{DisplayName} · {Confidence}";
    }

    private void ClearSession()
    {
        StopPlayback();
        session = null;
        replaySession = null;
        replayFrames = null;
        trailHistory = null;
        autoPauseIndex = null;
        trailVisibilityStart = 0;
        traceVisible = true;
        trailPointsVisible = true;
        TraceToggleButton.IsChecked = true;
        TrailPointsToggleButton.IsChecked = true;
        PaintSurface.SetTrailVisibility(showLines: true, showPoints: true);
        allRawRows = [];
        registerActivities = [];
        rawRowsById = [];
        decodedRowsBySourceId = [];
        decodedRows = [];
        diagnosticRows = [];
        diagnosticLineNumbers = [];
        reviewSession = null;
        reviewRows = [];
        reviewFilter = ReviewQueueFilter.All;
        selectedReviewGroupId = null;
        selectedMarkerId = null;
        baseDiagnostics = [];
        markers.Clear();
        ReplayTimelineSurface.SetMarkerFrames([]);
        decodeConfiguration = null;
        pendingSidecar = null;
        currentLogicalIndex = -1;
        currentInspectorRecord = null;
        currentInspectorFrame = null;
        currentInspectorSnapshot = null;
        currentInspectorPresentation = null;
        lastDetailPresentationTimestamp = 0;
        DetailPresentationCount = 0;
        loopIn = null;
        loopOut = null;
        loopEnabled = false;
        RawRecordsList.ItemsSource = null;
        RegisterSearchTextBox.Text = string.Empty;
        RegisterFilterComboBox.SelectedIndex = 0;
        configuringRegisterProfile = true;
        RegisterProfileComboBox.SelectedIndex = 0;
        configuringRegisterProfile = false;
        RegisterProfileComboBox.IsEnabled = false;
        ExportReadableLogButton.IsEnabled = false;
        RegisterActivitySurface.IsEnabled = false;
        RegisterActivitySurface.SetActivities([], 0, 1);
        RegisterActivitySurface.SetSelected(null);
        RegisterResultText.Text = "0 records · 0 register events";
        DecodedFramesList.ItemsSource = null;
        DiagnosticListBox.ItemsSource = null;
        ReviewOccurrenceComboBox.ItemsSource = null;
        ReviewActionsPanel.IsVisible = false;
        ReviewEmptyText.IsVisible = true;
        AnnotationMetadataPanel.IsVisible = false;
        MarkerQaCaseTextBox.Text = string.Empty;
        EventVersionComboBox.SelectedIndex = -1;
        EventVersionComboBox.IsEnabled = false;
        EditConfigurationButton.IsEnabled = false;
        Desay97ProfileComboBox.SelectedIndex = -1;
        Desay97ProfileComboBox.IsVisible = false;
        PaintTab.IsEnabled = false;
        PreviousFrameButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        NextFrameButton.IsEnabled = false;
        LoopToggleButton.IsEnabled = false;
        LoopToggleButton.IsChecked = false;
        AddMarkerButton.IsEnabled = false;
        SaveReviewButton.IsEnabled = false;
        LoadReviewButton.IsEnabled = false;
        ExportAnalysisButton.IsEnabled = false;
        ExportSelectedOutputButton.IsEnabled = false;
        AnalysisTab.IsEnabled = false;
        outputPreviewCancellation?.Cancel();
        outputPreviewCancellation?.Dispose();
        outputPreviewCancellation = null;
        currentOutputReport = null;
        ClearOutputVideoPreview();
        AnalysisHeatmapPreview.Show(null);
        AnalysisSummaryText.Text = "Decode a capture to prepare output preview.";
        OutputRangeText.Text = "-";
        OutputClockText.Text = "-";
        OutputFindingsText.Text = "-";
        OutputHotspotText.Text = "Waiting for preview";
        OutputConfigurationText.Text = "Decoder configuration will appear here.";
        OutputSourceText.Text = "Source identity will appear here.";
        AnalysisOutputText.Text = "Nothing exported in this session.";
        OutputContentComboBox.SelectedIndex = 0;
        LoadReviewButton.IsVisible = true;
        ApplySidecarButton.IsVisible = false;
        ReplayTimelineSurface.IsEnabled = false;
        ReplayTimelineSurface.SetMaximum(1);
        ReplayTimelineSurface.SetPosition(0);
        ReplayTimelineSurface.SetSupportingProgress(0, 0);
        ReplayTimelineSurface.SetLoopRange(null, null, false);
        ReplayClockText.Text = "00:00.000";
        ReplayEndClockText.Text = "00:00.000";
        LoopRangeText.Text = "Loop: off";
        PaintSurface.Fit();
        PaintZoomText.Text = "100%";
        PaintZoomHintBorder.IsVisible = false;
        PaintModeComboBox.SelectedIndex = 0;
        TrailModeComboBox.SelectedIndex = 1;
        TrailLengthComboBox.SelectedIndex = 5;
        TrailLengthComboBox.IsVisible = true;
        TrailLengthLabel.IsVisible = true;
        GridStrengthToggleButton.IsChecked = false;
        ReverseXToggleButton.IsChecked = false;
        ReverseYToggleButton.IsChecked = false;
        SwapAxesToggleButton.IsChecked = false;
        LegendPositionComboBox.SelectedIndex = 0;
        LegendVisibleToggleButton.IsChecked = true;
        LegendCompactToggleButton.IsChecked = true;
        reverseX = false;
        reverseY = false;
        swapAxes = false;
        legendPosition = ReplayLegendPosition.Auto;
        PaintSurface.SetStrongGrid(false);
        PaintSurface.SetLegendPosition(ReplayLegendPosition.TopLeft);
        PaintSurface.SetLegendVisible(true);
        PaintSurface.SetLegendCollapsed(true);
        PaintSurface.Clear();
        DiagnosticCountText.Text = "0";
        InspectorTitleText.Text = "Frame";
        InspectorSubtitleText.Text = "Select a physical record or decoded event.";
        InspectorLogicalText.Text = "-";
        InspectorTimestampText.Text = "-";
        InspectorFingerText.Text = "0";
        InspectorGloveText.Text = "0";
        InspectorPalmText.Text = "0";
        InspectorContactCountText.Text = "0 active";
        InspectorContactsList.ItemsSource = null;
        InspectorContactsList.SelectedItem = null;
        InspectorContactsList.IsVisible = false;
        InspectorNoContactsText.IsVisible = false;
        ProtocolFieldsItemsControl.ItemsSource = null;
        TransportFieldsItemsControl.ItemsSource = null;
        InspectorCrcText.Text = "-";
        InspectorAsilText.Text = "-";
        InspectorAsilRawText.Text = string.Empty;
        InspectorAllBreakText.Text = "ALL BREAK";
        InspectorAllBreakBadge.IsVisible = false;
        SetHealthText(InspectorCrcText, false, false);
        SetHealthText(InspectorAsilText, false, false);
        CollapsedFingerText.Text = "0";
        CollapsedGloveText.Text = "0";
        CollapsedPalmText.Text = "0";
        CollapsedAsilText.Text = "ASIL -";
        SetHealthState(CollapsedAsilBadge, false, false);
        InspectorAlertBorder.IsVisible = false;
        InspectorAlertText.Text = string.Empty;
        SourceAdapterText.Text = "Probing…";
        SourceConfidenceText.Text = "Source and Event Buffer format remain separate";
        SourceHashText.Text = "SHA-256 appears after loading";
        SourceFieldsText.Text = "No adapter-specific source fields.";
        SetTimelineCounts(0, 0, 0);
    }

    private void SetTimelineCounts(int physical, int logical, int evidence)
    {
        TimelinePhysicalCountText.Text = physical.ToString("N0", CultureInfo.InvariantCulture);
        TimelineLogicalCountText.Text = logical.ToString("N0", CultureInfo.InvariantCulture);
        TimelineEvidenceCountText.Text = evidence.ToString("N0", CultureInfo.InvariantCulture);
    }

    private void SetBusy(bool busy, string status)
    {
        operationInProgress = busy;
        LoadingProgress.IsVisible = busy;
        LoadButton.IsVisible = !busy;
        LoadButton.IsEnabled = !busy && !outputExportInProgress;
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

    private void InitializeReplay()
    {
        if (replaySession is null || session is null)
        {
            return;
        }

        var maximum = Math.Max(1, replaySession.Count - 1);
        PaintTab.IsEnabled = replaySession.Count > 0;
        SetSourceTransportEnabled(!outputWorkspaceActive);
        LoopToggleButton.IsEnabled = replaySession.Count > 1;
        ReplayTimelineSurface.IsEnabled = replaySession.Count > 0;
        ReplayTimelineSurface.SetMaximum(maximum);
        replayExtent = ReplayExtent.Measure(replaySession.AllReportedContacts);
        replayFrames = ReplayFrameCache.Create(replaySession);
        trailHistory = ReplayTrailHistory.Create(replayFrames.Snapshots);
        autoPauseIndex = ReplayAutoPauseIndex.Create(replayFrames.Snapshots);
        trailVisibilityStart = 0;
        traceVisible = true;
        trailPointsVisible = true;
        TraceToggleButton.IsChecked = true;
        TrailPointsToggleButton.IsChecked = true;
        PaintSurface.SetTrailVisibility(showLines: true, showPoints: true);
        RefreshLegendPlacement();
        PanelWidthTextBox.Text = replayExtent.MaximumX.ToString("0", CultureInfo.InvariantCulture);
        PanelHeightTextBox.Text = replayExtent.MaximumY.ToString("0", CultureInfo.InvariantCulture);
        SyncOutputResolutionWithPanel();
        PaintSurface.Fit();
        UpdatePaintZoomText();
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
    }

    private void SetSourceTransportEnabled(bool enabled)
    {
        var available = enabled && replaySession is { Count: > 0 };
        PreviousFrameButton.IsEnabled = available;
        PlayPauseButton.IsEnabled = available;
        NextFrameButton.IsEnabled = available;
    }

    private void SeekReplay(int logicalIndex, bool crossfade = false, bool synchronizeLists = true)
    {
        if (replaySession is null || replaySession.Count == 0)
        {
            return;
        }

        var clampedIndex = Math.Clamp(logicalIndex, 0, replaySession.Count - 1);
        var snapshot = replayFrames?[clampedIndex] ?? replaySession.Seek(clampedIndex);
        currentLogicalIndex = clampedIndex;
        RenderReplayFrame(snapshot, crossfade);
        UpdateReplayProgress(snapshot);

        if (synchronizeLists)
            SynchronizeReplayLists(clampedIndex, snapshot);

        if (synchronizeLists || ShouldPresentPlaybackDetails())
            PresentReplayDetails(snapshot);
    }

    private void RenderReplayFrame(ITouchReplaySnapshot snapshot, bool crossfade)
    {
        if (replaySession is null) return;
        PaintSurface.Show(ReplaySceneFactory.Create(
            snapshot,
            replaySession.Count,
            replayExtent,
            reviewSession?.Diagnostics,
            markers,
            traceVisible || trailPointsVisible
                ? trailHistory?.Build(snapshot.LogicalIndex, trailMode, trailLength, trailVisibilityStart) ?? []
                : [],
            reverseX,
            reverseY,
            swapAxes), crossfade);
    }

    private void UpdateReplayProgress(ITouchReplaySnapshot snapshot)
    {
        ReplayClockText.Text = FormatClock(SelectedTime(snapshot.Timeline));
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
        var physicalMaximum = Math.Max(1, session?.Records.Count - 1 ?? 1);
        var physicalValue = Math.Min(physicalMaximum, snapshot.PrimarySource.Index);
        var evidenceMaximum = Math.Max(1, diagnosticRows.Length);
        var evidenceValue = CountDiagnosticsThroughLine(snapshot.PrimarySource.Location.LineNumber);
        ReplayTimelineSurface.SetFrameState(
            snapshot.LogicalIndex,
            physicalValue / (double)physicalMaximum,
            evidenceValue / (double)evidenceMaximum);
    }

    private bool ShouldPresentPlaybackDetails()
    {
        if (!InspectorRailBorder.IsVisible || inspectorRailCollapsed) return false;
        var now = Stopwatch.GetTimestamp();
        if (lastDetailPresentationTimestamp != 0 &&
            Stopwatch.GetElapsedTime(lastDetailPresentationTimestamp, now) < PlaybackDetailPresentationInterval)
            return false;
        lastDetailPresentationTimestamp = now;
        return true;
    }

    private void PresentReplayDetails(ITouchReplaySnapshot snapshot)
    {
        lastDetailPresentationTimestamp = Stopwatch.GetTimestamp();
        DetailPresentationCount++;
        ShowRecord(snapshot.PhysicalRecords[^1], snapshot.DecodedFrame, snapshot);
    }

    private int CountDiagnosticsThroughLine(int lineNumber)
    {
        var low = 0;
        var high = diagnosticLineNumbers.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (diagnosticLineNumbers[middle] <= lineNumber) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void SynchronizeReplayLists(int logicalIndex, ITouchReplaySnapshot? snapshot = null)
    {
        if (replaySession is null || decodedRows.Length == 0 || logicalIndex < 0)
            return;

        var clampedIndex = Math.Clamp(logicalIndex, 0, decodedRows.Length - 1);
        snapshot ??= replayFrames?[clampedIndex] ?? replaySession.Seek(clampedIndex);
        synchronizingSelection = true;
        try
        {
            var decodedRow = decodedRows[clampedIndex];
            DecodedFramesList.SelectedItem = decodedRow;
            DecodedFramesList.ScrollIntoView(decodedRow);
            var primaryPhysical = snapshot.PhysicalRecords[^1];
            if (rawRowsById.TryGetValue(primaryPhysical.StableId, out var rawRow))
            {
                RawRecordsList.SelectedItem = rawRow;
                RawRecordsList.ScrollIntoView(rawRow);
            }
        }
        finally
        {
            synchronizingSelection = false;
        }
    }

    private void UpdateAutoPauseIndicator(ReplayPlaybackPreferences preferences)
    {
        var enabled = new List<string>();
        if (preferences.PauseOnAlarmOrQaFail) enabled.Add("Alarm / QA Fail");
        if (preferences.PauseOnBreak) enabled.Add("Break");
        if (preferences.PauseOnAllBreak) enabled.Add("All Break");
        AutoPauseSettingsButton.Classes.Set("active", enabled.Count > 0);
        ToolTip.SetTip(AutoPauseSettingsButton,
            enabled.Count == 0 ? "Auto-pause is disabled" : $"Auto-pause · {string.Join(" · ", enabled)}");
    }

    private void PreviousFrameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekReplay(currentLogicalIndex - 1);
    }

    private void NextFrameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekReplay(currentLogicalIndex + 1);
    }

    private void PlayPauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (playbackCancellation is not null)
        {
            StopPlayback();
            return;
        }

        if (replaySession is null || replaySession.Count == 0)
        {
            return;
        }
        if (loopEnabled && loopIn is { } loopStart && loopOut is { } loopEnd && loopStart >= loopEnd)
        {
            TimelineStatusText.Text = "Loop needs at least 2 frames · drag either Loop handle to widen the range";
            return;
        }
        if (currentLogicalIndex >= replaySession.Count - 1)
        {
            SeekReplay(loopEnabled ? loopIn ?? 0 : 0);
        }
        var cancellation = new CancellationTokenSource();
        playbackCancellation = cancellation;
        var generation = ++playbackRunGeneration;
        SetPlaybackVisualState(playing: true);
        UpdatePlaybackStatus();
        _ = RunPlaybackAsync(cancellation.Token, generation);
    }

    private async Task RunPlaybackAsync(CancellationToken cancellationToken, int generation)
    {
        var playbackStarted = Stopwatch.GetTimestamp();
        var scheduledElapsed = TimeSpan.Zero;
        var timingRevision = playbackTimingRevision;
        try
        {
            while (replaySession is { Count: > 0 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timingRevision != playbackTimingRevision)
                {
                    playbackStarted = Stopwatch.GetTimestamp();
                    scheduledElapsed = TimeSpan.Zero;
                    timingRevision = playbackTimingRevision;
                }

                var end = loopEnabled ? loopOut ?? replaySession.Count - 1 : replaySession.Count - 1;
                if (currentLogicalIndex >= end)
                {
                    if (loopEnabled && loopIn is { } start && start <= end)
                    {
                        if (start >= end)
                        {
                            StopPlaybackIfCurrent(
                                generation,
                                "Loop needs at least 2 frames · drag either Loop handle to widen the range");
                            return;
                        }
                        SeekReplay(start, crossfade: true, synchronizeLists: false);
                        playbackStarted = Stopwatch.GetTimestamp();
                        scheduledElapsed = TimeSpan.Zero;
                        continue;
                    }
                    StopPlaybackIfCurrent(generation, "Replay complete · use Home, seek, or Play to restart");
                    return;
                }

                int next;
                PlaybackPauseDecision? pauseDecision;
                if (maxReplaySpeed)
                {
                    var interval = TimeSpan.FromMilliseconds(16);
                    var firstDue = scheduledElapsed + interval;
                    await DelayUntilAsync(playbackStarted, firstDue, cancellationToken);
                    var elapsed = Stopwatch.GetElapsedTime(playbackStarted);
                    var elapsedTicks = Math.Max(interval.Ticks, elapsed.Ticks - scheduledElapsed.Ticks);
                    var dueSteps = Math.Max(1L, elapsedTicks / interval.Ticks);
                    var availableSteps = Math.Max(1L, (end - currentLogicalIndex + 49L) / 50L);
                    var steps = Math.Min(dueSteps, availableSteps);
                    var frameAdvance = Math.Min((long)end - currentLogicalIndex, steps * 50L);
                    next = currentLogicalIndex + (int)frameAdvance;
                    pauseDecision = FirstPlaybackPause(currentLogicalIndex, next);
                    if (pauseDecision is { } decision) next = decision.LogicalIndex;
                    scheduledElapsed += TimeSpan.FromTicks(interval.Ticks * steps);
                }
                else
                {
                    var firstDue = ReplayPlaybackSchedule.NextVisualDue(
                        scheduledElapsed,
                        DelayBetween(currentLogicalIndex, currentLogicalIndex + 1),
                        MinimumPlaybackVisualInterval);
                    await DelayUntilAsync(playbackStarted, firstDue, cancellationToken);
                    pauseDecision = FirstPlaybackPause(currentLogicalIndex, end);
                    var position = ReplayPlaybackSchedule.CatchUp(
                        currentLogicalIndex,
                        end,
                        scheduledElapsed,
                        Stopwatch.GetElapsedTime(playbackStarted),
                        DelayBetween,
                        pauseDecision?.LogicalIndex);
                    next = position.LogicalIndex;
                    scheduledElapsed = position.ScheduledElapsed;
                }

                SeekReplay(next, synchronizeLists: false);
                if (pauseDecision?.LogicalIndex == next)
                {
                    if (pauseDecision.SelectReview) SelectReviewAt(next);
                    StopPlaybackIfCurrent(generation, $"Paused on {pauseDecision.Reason} · continue when ready");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StopPlaybackIfCurrent(generation, $"Replay stopped after an error · {exception.Message}");
        }
    }

    private void StopPlaybackIfCurrent(int generation, string status)
    {
        if (generation == playbackRunGeneration)
            StopPlayback(status);
    }

    private static async Task DelayUntilAsync(
        long startedTimestamp,
        TimeSpan scheduledElapsed,
        CancellationToken cancellationToken)
    {
        var waited = false;
        while (true)
        {
            var remaining = scheduledElapsed - Stopwatch.GetElapsedTime(startedTimestamp);
            if (remaining <= TimeSpan.Zero) break;
            var slice = remaining > TimeSpan.FromSeconds(1)
                ? TimeSpan.FromSeconds(1)
                : remaining < TimeSpan.FromMilliseconds(1)
                    ? TimeSpan.FromMilliseconds(1)
                    : remaining;
            await Task.Delay(slice, cancellationToken);
            waited = true;
        }
        if (!waited)
            await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void SelectReviewAt(int logicalIndex)
    {
        if (reviewSession is null) return;
        reviewFilter = ReviewQueueFilter.All;
        RefreshReviewQueue();
        var row = reviewRows.FirstOrDefault(item => item.Group.Occurrences.Any(occurrence =>
            occurrence.LogicalIndex == logicalIndex &&
            (occurrence.Diagnostic.Severity == DiagnosticSeverity.Alarm || occurrence.Category == ReviewEventCategory.QaResult)));
        if (row is null) return;
        DiagnosticListBox.SelectedItem = row;
        DiagnosticListBox.ScrollIntoView(row);
    }

    private PlaybackPauseDecision? FirstPlaybackPause(int exclusiveStart, int inclusiveEnd)
    {
        var reviewIndex = reviewSession?.FirstPauseIndex(exclusiveStart, inclusiveEnd);
        var preferences = SettingsPage.PlaybackPreferences;
        var automatic = autoPauseIndex?.FirstAfter(
            exclusiveStart,
            inclusiveEnd,
            preferences.PauseOnBreak,
            preferences.PauseOnAllBreak);
        if (reviewIndex is { } review && (automatic is null || review <= automatic.LogicalIndex))
            return new PlaybackPauseDecision(review, "Alarm / QA Fail", SelectReview: true);
        return automatic is null
            ? null
            : new PlaybackPauseDecision(
                automatic.LogicalIndex,
                automatic.Kind == ReplayAutomaticPauseKind.AllBreak ? "All Break" : "reported Break",
                SelectReview: false);
    }

    private sealed record PlaybackPauseDecision(int LogicalIndex, string Reason, bool SelectReview);

    private TimeSpan DelayBetween(int current, int next)
    {
        if (replaySession is null)
        {
            return TimeSpan.Zero;
        }
        var currentEntry = replaySession.Timeline[current];
        var nextEntry = replaySession.Timeline[next];
        TimeSpan delay;
        if (ClockModeComboBox.SelectedIndex == 1)
        {
            delay = nextEntry.FrameTime - currentEntry.FrameTime;
        }
        else if (CompressIdleCheckBox.IsChecked == true)
        {
            delay = nextEntry.DisplayTime - currentEntry.DisplayTime;
        }
        else
        {
            delay = nextEntry.RecordedTime - currentEntry.RecordedTime;
        }
        return TimeSpan.FromTicks(Math.Max(0, (long)(delay.Ticks / replaySpeed)));
    }

    private void UpdatePlaybackStatus()
    {
        if (replaySession is null || replaySession.Count == 0 || currentLogicalIndex < 0)
            return;
        var nextIndex = Math.Min(currentLogicalIndex + 1, replaySession.Count - 1);
        TimelineStatusText.Text =
            $"Playing {(maxReplaySpeed ? "MAX" : $"{replaySpeed:0.###}×")} · " +
            $"next {DelayBetween(currentLogicalIndex, nextIndex).TotalMilliseconds:0.###} ms · " +
            $"frame interval {replaySession.FrameInterval.TotalMilliseconds:0.###} ms";
    }

    private void RestartPlaybackTimingIfActive()
    {
        var previous = playbackCancellation;
        if (previous is null || replaySession is null || replaySession.Count == 0)
            return;

        var replacement = new CancellationTokenSource();
        playbackCancellation = replacement;
        var generation = ++playbackRunGeneration;
        previous.Cancel();
        previous.Dispose();
        UpdatePlaybackStatus();
        _ = RunPlaybackAsync(replacement.Token, generation);
    }

    private void StopPlayback(string? status = null)
    {
        var cancellation = playbackCancellation;
        playbackCancellation = null;
        playbackRunGeneration++;
        cancellation?.Cancel();
        cancellation?.Dispose();
        if (cancellation is not null && replaySession is { Count: > 0 } && currentLogicalIndex >= 0)
        {
            SynchronizeReplayLists(currentLogicalIndex);
            if (replayFrames is { } frames) PresentReplayDetails(frames[currentLogicalIndex]);
        }
        if (PlayPauseButton is not null)
        {
            SetPlaybackVisualState(playing: false);
        }
        if (TimelineStatusText is not null && replaySession is { Count: > 0 } &&
            (status is not null || cancellation is not null))
        {
            TimelineStatusText.Text = status ?? "Paused · Space play/pause · ←/→ step · drag Loop handles to set range";
        }
    }

    private void SetPlaybackVisualState(bool playing)
    {
        PlayPauseButton.Classes.Set("playing", playing);
        PlayPauseButton.Content = playing ? "Ⅱ  Pause" : "▶  Play";
        AutomationProperties.SetName(PlayPauseButton, playing ? "Pause replay" : "Play replay");
    }

    private void ReplayTimelineSurface_OnSeekRequested(object? sender, ReplayTimelineSeekEventArgs e)
    {
        if (!synchronizingSelection && replaySession is not null)
        {
            StopPlayback();
            SeekReplay(e.LogicalIndex);
        }
    }

    private void ReplaySpeedComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var tag = item.Tag?.ToString() ?? "1";
        var nextMaximum = tag.Equals("max", StringComparison.OrdinalIgnoreCase);
        var nextSpeed = replaySpeed;
        if (!nextMaximum && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            nextSpeed = speed;
        }
        if (nextMaximum == maxReplaySpeed && Math.Abs(nextSpeed - replaySpeed) < 0.0001) return;
        maxReplaySpeed = nextMaximum;
        replaySpeed = nextSpeed;
        NotifyPlaybackTimingChanged();
    }

    private void ClockModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CompressIdleCheckBox is not null)
            CompressIdleCheckBox.IsVisible = ClockModeComboBox.SelectedIndex == 0;
        NotifyPlaybackTimingChanged();
        if (replaySession is not null && currentLogicalIndex >= 0)
        {
            ReplayClockText.Text = FormatClock(SelectedTime(replaySession.Timeline[currentLogicalIndex]));
            ReplayEndClockText.Text = FormatClock(SelectedEndTime());
            if (currentInspectorRecord is not null)
                ShowRecord(currentInspectorRecord, currentInspectorFrame, currentInspectorSnapshot);
        }
    }

    private void ReplayTimingOption_OnChanged(object? sender, RoutedEventArgs e) => NotifyPlaybackTimingChanged();

    private void NotifyPlaybackTimingChanged()
    {
        playbackTimingRevision++;
        RestartPlaybackTimingIfActive();
    }

    private void PaintModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not SelectOption option ||
            !Enum.TryParse<ReplayRenderMode>(option.Value, out var mode))
            return;
        if (PaintSurface?.Mode == mode) return;
        PaintSurface?.SetMode(mode);
        if (currentInspectorRecord is not null)
            ShowRecord(currentInspectorRecord, currentInspectorFrame, currentInspectorSnapshot);
        RefreshOutputPreviewIfVisible();
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

        if (Math.Abs(replayExtent.MaximumX - width) < 0.001 &&
            Math.Abs(replayExtent.MaximumY - height) < 0.001)
            return;

        replayExtent = new ReplayExtent(width, height);
        SyncOutputResolutionWithPanel();
        PaintSurface.Fit();
        RefreshLegendPlacement();
        UpdatePaintZoomText();
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
        StopOutputVideoPreviewPlayback();
        RefreshOutputPreviewIfVisible();
        SessionStatusText.Text = $"Panel coordinate space · {width:0} × {height:0} · fit to canvas";
    }

    private void PaintFitButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PaintSurface.Fit();
        RefreshLegendPlacement();
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
        if ((sender as ComboBox)?.SelectedItem is not SelectOption option ||
            !Enum.TryParse<ReplayTrailMode>(option.Value, out var selectedMode))
            return;

        if (trailMode == selectedMode) return;

        trailMode = selectedMode;
        if (TrailLengthComboBox is not null)
            TrailLengthComboBox.IsVisible = true;
        if (TrailLengthLabel is not null)
            TrailLengthLabel.IsVisible = true;
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
    }

    private void TrailLengthComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedLength))
            return;

        if (trailLength == selectedLength) return;

        trailLength = selectedLength;
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
    }

    private void TraceToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        traceVisible = TraceToggleButton.IsChecked == true;
        PaintSurface.SetTrailVisibility(traceVisible, trailPointsVisible);
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
        SessionStatusText.Text = traceVisible
            ? "Touch traces visible"
            : "Touch traces hidden · source data unchanged";
    }

    private void TrailPointsToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        trailPointsVisible = TrailPointsToggleButton.IsChecked == true;
        PaintSurface.SetTrailVisibility(traceVisible, trailPointsVisible);
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
        SessionStatusText.Text = trailPointsVisible
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
        if ((sender as ComboBox)?.SelectedItem is not SelectOption option ||
            !Enum.TryParse<ReplayLegendPosition>(option.Value, out var selectedPosition))
            return;

        if (legendPosition == selectedPosition) return;

        legendPosition = selectedPosition;
        RefreshLegendPlacement();
    }

    private void LegendVisibleToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        PaintSurface.SetLegendVisible(LegendVisibleToggleButton.IsChecked == true);
        RefreshCurrentOutputVideoFrame();
    }

    private void LegendCompactToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        PaintSurface.SetLegendCollapsed(LegendCompactToggleButton.IsChecked == true);
        RefreshCurrentOutputVideoFrame();
    }

    private void PaintSurface_OnLegendCollapsedChanged(object? sender, EventArgs e)
    {
        if (LegendCompactToggleButton is not null)
            LegendCompactToggleButton.IsChecked = PaintSurface.LegendCollapsed;
    }

    private void RefreshLegendPlacement()
    {
        var resolved = legendPosition;
        if (resolved == ReplayLegendPosition.Auto)
        {
            resolved = replaySession is null
                ? ReplayLegendPosition.TopLeft
                : ReplayLegendPositioner.Choose(replaySession.AllReportedContacts, replayExtent, reverseX, reverseY, swapAxes);
        }
        PaintSurface.SetLegendPosition(resolved);
        RefreshCurrentOutputVideoFrame();
    }

    private void ReverseAxisToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        reverseX = ReverseXToggleButton.IsChecked == true;
        reverseY = ReverseYToggleButton.IsChecked == true;
        swapAxes = SwapAxesToggleButton.IsChecked == true;
        RefreshLegendPlacement();
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
    }

    private void GridStrengthToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var strong = GridStrengthToggleButton.IsChecked == true;
        GridStrengthToggleButton.Content = strong ? "Grid +" : "Grid";
        PaintSurface.SetStrongGrid(strong);
        RefreshCurrentOutputVideoFrame();
    }

    private void LoopInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0)
        {
            return;
        }
        loopIn = currentLogicalIndex;
        if (loopOut < loopIn)
        {
            loopOut = null;
        }
        EnableLoopFromShortcut();
        UpdateLoopText();
    }

    private void LoopOutButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0)
        {
            return;
        }
        loopOut = currentLogicalIndex;
        if (loopIn > loopOut)
        {
            loopIn = null;
        }
        EnableLoopFromShortcut();
        UpdateLoopText();
    }

    private void LoopToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingLoopControls || ReplayTimelineSurface is null) return;
        loopEnabled = LoopToggleButton.IsChecked == true;
        if (loopEnabled && replaySession is { Count: > 1 })
        {
            if (loopIn is null || loopOut is null)
            {
                var range = ReplayLoopRangePlanner.CreateDefault(
                    currentLogicalIndex,
                    replaySession.Count,
                    replaySession.FrameInterval);
                loopIn = range.StartLogicalIndex;
                loopOut = range.EndLogicalIndex;
            }
        }
        NotifyPlaybackTimingChanged();
        UpdateLoopText();
    }

    private void ReplayTimelineSurface_OnLoopRangeChanged(object? sender, ReplayTimelineLoopEventArgs e)
    {
        loopIn = e.StartLogicalIndex;
        loopOut = e.EndLogicalIndex;
        loopEnabled = true;
        NotifyPlaybackTimingChanged();
        UpdateLoopText();
    }

    private void EnableLoopFromShortcut()
    {
        if (loopIn is null || loopOut is null) return;
        loopEnabled = true;
        synchronizingLoopControls = true;
        try
        {
            LoopToggleButton.IsChecked = true;
        }
        finally
        {
            synchronizingLoopControls = false;
        }
        NotifyPlaybackTimingChanged();
    }

    private void UpdateLoopText()
    {
        var hasRange = loopIn is not null && loopOut is not null;
        LoopRangeText.Text = loopEnabled && hasRange
            ? $"Loop: on · {loopIn!.Value + 1:N0}-{loopOut!.Value + 1:N0}"
            : "Loop: off";
        ReplayTimelineSurface.SetLoopRange(loopIn, loopOut, loopEnabled);
        RefreshOutputPreviewIfVisible();
    }

    private void RefreshOutputPreviewIfVisible()
    {
        if (WorkspaceTabs?.SelectedItem == AnalysisTab)
            _ = RefreshOutputPreviewAsync();
    }

    private TimeSpan SelectedTime(ReplayTimelineEntry entry) =>
        ClockModeComboBox.SelectedIndex == 1 ? entry.FrameTime : entry.RecordedTime;

    private TimeSpan SelectedEndTime()
    {
        if (replaySession is null || replaySession.Count == 0)
        {
            return TimeSpan.Zero;
        }
        return SelectedTime(replaySession.Timeline[^1]);
    }

    private static string FormatClock(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

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

    private void JumpFrames(int delta)
    {
        StopPlayback();
        SeekReplay(currentLogicalIndex + delta);
    }

    private void SelectRelativeReplaySpeed(int delta)
    {
        var items = ReplaySpeedComboBox.Items.OfType<ComboBoxItem>().ToArray();
        if (items.Length == 0) return;
        ReplaySpeedComboBox.SelectedIndex = Math.Clamp(ReplaySpeedComboBox.SelectedIndex + delta, 0, items.Length - 1);
        TimelineStatusText.Text = $"Replay speed · {items[ReplaySpeedComboBox.SelectedIndex].Content}";
    }

    private void SelectNormalReplaySpeed()
    {
        var items = ReplaySpeedComboBox.Items.OfType<ComboBoxItem>().ToArray();
        var normalIndex = Array.FindIndex(items, item => string.Equals(item.Tag?.ToString(), "1", StringComparison.Ordinal));
        if (normalIndex < 0) return;
        ReplaySpeedComboBox.SelectedIndex = normalIndex;
        TimelineStatusText.Text = "Replay speed · 1×";
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
        ExitOutputFullscreen();
        StopPlayback();
        StopOutputVideoPreviewPlayback();
        outputPreviewCancellation?.Cancel();
        outputPreviewCancellation?.Dispose();
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        outputExportCancellation?.Cancel();
        outputExportCancellation?.Dispose();
        if (Application.Current is { } application) application.ActualThemeVariantChanged -= Application_OnActualThemeVariantChanged;
        base.OnClosed(e);
    }

    private static string FormatBytes(IReadOnlyList<byte> data)
    {
        const int displayLimit = 4096;
        var count = Math.Min(data.Count, displayLimit);
        var builder = new StringBuilder(count * 3);
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(index % 16 == 0 ? Environment.NewLine : ' ');
            }

            builder.Append(data[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (data.Count > displayLimit)
        {
            builder.AppendLine();
            builder.Append($"… {data.Count - displayLimit:N0} additional bytes retained in the session");
        }

        return builder.ToString();
    }

    private static string FormatDecodedFields(CommonEventBufferFrame frame)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"touches       {frame.NumTouches}");
        builder.AppendLine($"all break     {frame.AllBreak}");
        builder.AppendLine($"crc           {(frame.CrcValid ? "OK" : $"FAIL ({frame.CapturedCrc:X2}/{frame.ComputedCrc:X2})")}");
        builder.AppendLine($"ASIL byte     0x{frame.Asil.Raw:X2}");
        builder.AppendLine($"button valid  {frame.Button.Valid}");
        if (frame.BusStatus is { } bus)
        {
            builder.AppendLine($"bus counters  no-response={bus.NoResponseCounter} busy={bus.BusyCounter}");
        }

        if (frame.DiagnosticPacket is { } packet)
        {
            builder.AppendLine($"EMS bitmap    {Convert.ToString(packet.EmsBitmap, 2).PadLeft(4, '0')}");
            builder.AppendLine($"global palm   {packet.PalmOn}");
        }

        if (frame.Fingers.Any(IsReportedFinger))
            builder.AppendLine("\nCONTACTS      TYPE     STATE        X     Y");
        foreach (var finger in frame.Fingers.Where(IsReportedFinger))
        {
            builder.AppendLine($"#{finger.Id,-2}           {finger.Type,-8} {finger.Status,-8} {finger.X,5} {finger.Y,5}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDecodedFields(Desay97Frame frame)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"profile       {ProfileText(frame.Profile)}");
        builder.AppendLine($"touches       {frame.NumTouches}");
        builder.AppendLine($"all break     {frame.AllBreak}");
        builder.AppendLine($"crc           {(frame.CrcValid ? "OK" : $"FAIL ({frame.CapturedCrc:X2}/{frame.ComputedCrc:X2})")}");
        builder.AppendLine($"short/open    {frame.Short}/{frame.Open}");
        builder.AppendLine($"TP ASIL       {frame.TpAsilError}");
        builder.AppendLine($"phase 1       Physical #{frame.Packet.Probe.Index} · L{frame.Packet.Probe.Location.LineNumber}");
        builder.AppendLine($"phase 2       Physical #{frame.Packet.PayloadRead.Index} · L{frame.Packet.PayloadRead.Location.LineNumber}");
        if (frame.Fingers.Count > 0)
            builder.AppendLine("\nCONTACTS      TYPE     STATE        X     Y");
        foreach (var finger in frame.Fingers)
        {
            var semantic = finger.Invalid ? "Invalid" : finger.Palm ? "Palm" : "Finger";
            builder.AppendLine($"#{finger.Id,-2}           {semantic,-8} {finger.Status,-8} {finger.X,5} {finger.Y,5}");
        }
        return builder.ToString().TrimEnd();
    }

    private static bool IsReportedFinger(CommonFinger finger) =>
        finger.IsReported;

    private static string VersionText(CommonEventBufferVersion version) => version switch
    {
        CommonEventBufferVersion.V82 => "0x82",
        CommonEventBufferVersion.V83 => "0x83",
        CommonEventBufferVersion.V84 => "0x84",
        CommonEventBufferVersion.V85 => "0x85",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static string ProfileText(Desay97Profile profile) => profile switch
    {
        Desay97Profile.Standard => "Standard",
        Desay97Profile.BenzPalm => "Benz Palm",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

}
