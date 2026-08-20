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
    private ReviewInspectorWorkspace? reviewWorkspace;
    private ReviewGroupRow[] reviewRows = [];
    private ReplayDecodeConfiguration? decodeConfiguration;
    private static readonly ReplayExtent DefaultPaintExtent = new(2304, 1280);
    private CancellationTokenSource? playbackCancellation;
    private CancellationTokenSource? outputPreviewCancellation;
    private CancellationTokenSource? outputVideoPlaybackCancellation;
    private readonly ReplayOutputWorkspace outputWorkspace = new();
    private ReplayOutputPlanService outputPlanService = new();
    private readonly ExportJobService outputExportJobs = new();
    private ReplayOutputPreviewPlan? outputVideoPreviewPlan;
    private ITouchReplaySession? outputVideoPreviewReplay;
    private int outputVideoFrameIndex;
    private bool outputVideoPreviewDirty;
    private long outputPreviewGeneration;
    private bool outputPreviewPlanPending;
    private string? outputPreviewPlanError;
    private OutputReportService outputReportService = new();
    private CaptureAnalysisReport? currentOutputReport;
    private readonly ReplayPlaybackController playbackController = new();
    private readonly CaptureDecodeController captureDecodeController = new();
    private ReplayPaintWorkspace? paintWorkspace;
    private long nextCaptureLoadGeneration;
    private long activeCaptureLoadGeneration;
    private long activeCaptureDecodeGeneration;
    private int heatmapMinimumCount = 5;
    private double heatmapLabelThresholdRatio = 0.65;
    private string appliedHeatmapMode = "all";
    private bool outputFullscreenActive;
    private WindowState outputFullscreenPreviousWindowState = WindowState.Normal;
    private int currentLogicalIndex => playbackController.CurrentIndex;
    private int? loopIn => playbackController.LoopStart;
    private int? loopOut => playbackController.LoopEnd;
    private bool loopEnabled => playbackController.LoopEnabled;
    private double replaySpeed => playbackController.Rate.Multiplier;
    private ReplayAutoPauseIndex? autoPauseIndex;
    private int zoomHintRevision;
    private bool maxReplaySpeed => playbackController.Rate.IsMaximum;
    private bool synchronizingSelection;
    private bool synchronizingLoopControls;
    private bool configuringEventVersion;
    private string? pendingSourcePath;
    private bool configuringSourceChoice;
    private bool configuringRegisterProfile;
    private bool configuringOutputSettings;
    private bool synchronizingPaintControls;
    private bool continuousTimelineSeekScheduled;
    private int? pendingContinuousTimelineSeek;
    private bool operationInProgress;
    private bool outputWorkspaceActive;
    private int themeMode;
    private bool reviewRailCollapsed;
    private bool inspectorRailCollapsed;
    private bool? reviewRailUserPreference;
    private bool? inspectorRailUserPreference;
    private SourceRecord? currentInspectorRecord;
    private object? currentInspectorFrame;
    private ITouchReplaySnapshot? currentInspectorSnapshot;
    private InspectorFramePresentation? currentInspectorPresentation;
    private bool synchronizingReviewSelection;
    private double expandedInspectorRailWidth = 286;
    private const double ExpandedReviewRailWidth = 260;
    private const double DefaultInspectorRailWidth = 286;
    private const double MinimumInspectorRailWidth = 280;
    private const double MaximumInspectorRailWidth = 420;
    private const double CollapsedInspectorRailWidth = 58;
    private const double ReplayTransportHeight = 64;
    private static readonly TimeSpan PlaybackDetailPresentationInterval = TimeSpan.FromMilliseconds(100);
    private long lastDetailPresentationTimestamp;
    internal Action<CaptureLoadProgress>? CaptureLoadProgressObserver { get; set; }
    internal Action<CaptureDecodeProgress>? CaptureDecodeProgressObserver { get; set; }
    internal int DetailPresentationCount { get; private set; }
    internal ReplayOutputSettings OutputSettings => outputWorkspace.Settings;
    internal long OutputSettingsRevision => outputWorkspace.Revision;
    internal ReplayOutputPlanIdentity? OutputPreviewPlanIdentity => outputVideoPreviewPlan?.Identity;
    internal ReplayFramePlanSnapshot? OutputPreviewFramePlan => outputVideoPreviewPlan?.FramePlan;
    internal int OutputPreviewPlanBuildCount => outputPlanService.BuildCount;
    internal int OutputReportBuildCount => outputReportService.BuildCount;
    internal int OutputVideoRenderCount => OutputVideoPreview.RgbRenderCount;
    internal bool OutputVideoPreviewDirty => outputVideoPreviewDirty;
    internal CaptureAnalysisReport? CurrentOutputReport => currentOutputReport;
    internal OutputReportInputs CaptureCurrentOutputReportInputsForTesting(AnalysisRange range) =>
        CaptureOutputReportInputs(range);
    internal string OutputReplayIdentityForTesting => OutputReplayIdentity();
    internal void ReplaceOutputPlanServiceForTesting(ReplayOutputPlanService replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ResetOutputPlanning();
        outputPlanService.Dispose();
        outputPlanService = replacement;
        ClearOutputVideoPreview();
    }
    internal void ReplaceOutputReportServiceForTesting(OutputReportService replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        outputReportService.Reset();
        outputReportService.Dispose();
        outputReportService = replacement;
        currentOutputReport = null;
    }
    internal ReplayPaintWorkspace? PaintWorkspace => paintWorkspace;
    internal ReviewInspectorWorkspace? ReviewWorkspace => reviewWorkspace;
    private int outputVideoFrameCount => outputVideoPreviewPlan?.Estimate.OutputFrameCount ?? 0;
    private int outputVideoFrameRate => outputVideoPreviewPlan?.Identity.Settings.FrameRate ?? outputWorkspace.Settings.FrameRate;
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
        SynchronizeVisibleDecodedInspector();
        if (currentInspectorPresentation is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(InspectorPresentationBuilder.BuildCopyText(currentInspectorPresentation));
        SessionStatusText.Text = $"Copied frame {currentInspectorPresentation.LogicalLabel} summary";
    }

    private void PreviousFindingButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFinding(-1);

    private void NextFindingButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFinding(1);

    private void NavigateFinding(int direction)
    {
        if (reviewWorkspace is null || replaySession is null) return;
        var occurrence = reviewWorkspace.NavigateFinding(direction < 0
            ? ReviewFindingDirection.Previous
            : ReviewFindingDirection.Next);
        if (occurrence?.LogicalIndex is not { } target) return;
        StopPlayback();
        SeekReplay(target);
        SynchronizeReviewSelectionFromWorkspace(scrollIntoView: true);
    }

    private void InspectorContactsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedId = (InspectorContactsList.SelectedItem as InspectorContactRow)?.Id;
        if (reviewWorkspace is not null)
        {
            if (selectedId is { } id) reviewWorkspace.SelectContact(id);
            else reviewWorkspace.ClearContactSelection();
        }
        PaintSurface.SetHighlightedContact(selectedId);
    }

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
        var loadGeneration = checked(++nextCaptureLoadGeneration);
        activeCaptureLoadGeneration = loadGeneration;
        captureDecodeController.CancelCurrent();
        activeCaptureDecodeGeneration = 0;
        ResetOutputPlanning();
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        var loadCancellation = new CancellationTokenSource();
        operationCancellation = loadCancellation;
        var cancellationToken = loadCancellation.Token;
        SetBusy(true, "Opening capture");

        try
        {
            var progress = new InlineProgress<CaptureLoadProgress>(item =>
            {
                CaptureLoadProgressObserver?.Invoke(item);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested ||
                        !IsActiveCaptureLoad(loadGeneration, loadCancellation))
                        return;
                    var count = item.RecordsRead > 0 ? $" · {item.RecordsRead:N0} records" : string.Empty;
                    SessionStatusText.Text = item.Phase + count;
                });
            });
            var nextSession = await Task.Run(
                () => CaptureSession.LoadAsync(path, progress, cancellationToken, adapterId),
                cancellationToken);
            var projectedActivities = await Task.Run(
                () => RegisterActivityProjector.Project(nextSession.Records, nextSession.RegisterAnnotations).ToArray(),
                cancellationToken);
            var nextDiagnostics = SessionDiagnostics(nextSession);
            var nextDiagnosticRows = nextDiagnostics
                .Select(diagnostic => new DiagnosticRow(diagnostic))
                .ToArray();

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActiveCaptureLoad(loadGeneration, loadCancellation))
                throw new OperationCanceledException(cancellationToken);
            ClearSession();
            session = nextSession;
            ApplyRawExplorerProjection(projectedActivities);
            diagnosticRows = nextDiagnosticRows;
            CreateReviewWorkspace(nextDiagnostics, []);
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
            if (IsActiveCaptureLoad(loadGeneration, loadCancellation))
                SessionStatusText.Text = "Load cancelled; no partial session was committed";
        }
        catch (SourceSelectionRequiredException exception)
        {
            if (IsActiveCaptureLoad(loadGeneration, loadCancellation))
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
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (IsActiveCaptureLoad(loadGeneration, loadCancellation))
            {
                SessionStatusText.Text = $"Load failed · {exception.Message}";
                ConfigurationHintText.Text = "Choose another file, inspect its schema, or select a supported adapter explicitly.";
            }
        }
        finally
        {
            if (IsActiveCaptureLoad(loadGeneration, loadCancellation))
            {
                activeCaptureLoadGeneration = 0;
                SetBusy(false, SessionStatusText.Text ?? "Ready");
            }
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

    private async Task DecodeSelectedAsync(
        bool preserveWorkspaceContext = false,
        ReviewWorkspaceState? preservedWorkspaceStateOverride = null,
        TabItem? preservedWorkspaceTabOverride = null)
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

        var preservedWorkspaceState = preservedWorkspaceStateOverride ?? CaptureReviewWorkspaceState();
        var preservedWorkspaceTab = preservedWorkspaceTabOverride ?? WorkspaceTabs.SelectedItem as TabItem;
        var preservedPaintSettings = paintWorkspace?.Settings;

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        ResetOutputPlanning();
        var decodeCancellation = new CancellationTokenSource();
        operationCancellation = decodeCancellation;
        var cancellationToken = decodeCancellation.Token;
        SetBusy(true, $"Decoding {(isDesay97 ? "Desay" : "Common")} {versionText}");
        long operationGeneration = 0;

        try
        {
            var loadedSession = session;
            var request = new PreparedCaptureDecodeRequest(
                loadedSession,
                new FormatDecodeRequest(
                    versionText,
                    loadedSession.RegisterProfile,
                    isDesay97
                        ? desayProfile == Desay97Profile.BenzPalm ? "benz-palm" : "standard"
                        : null));
            var progress = new InlineProgress<CaptureDecodeProgress>(item =>
            {
                CaptureDecodeProgressObserver?.Invoke(item);
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(operationCancellation, decodeCancellation))
                        PresentCaptureDecodeProgress(item);
                });
            });
            var operation = captureDecodeController.StartPrepared(request, progress, cancellationToken);
            operationGeneration = operation.Generation;
            activeCaptureDecodeGeneration = operationGeneration;
            var result = await operation.Completion;
            var presentation = await Task.Run(
                () => BuildCaptureDecodePresentation(result, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (activeCaptureDecodeGeneration != result.Generation ||
                !ReferenceEquals(operationCancellation, decodeCancellation) ||
                !ReferenceEquals(captureDecodeController.LastSuccessfulResult, result) ||
                !ReferenceEquals(session, loadedSession))
                throw new CaptureDecodeSupersededException(result.Generation);

            var nextConfiguration = result.Decode.Configuration;

            cancellationToken.ThrowIfCancellationRequested();
            var preserveReview = preserveWorkspaceContext || decodeConfiguration is null || decodeConfiguration == nextConfiguration;
            var preservedMarkers = preserveReview ? reviewWorkspace?.Markers.ToArray() ?? [] : [];
            var preservedReviewState = preserveReview ? reviewWorkspace?.ReviewSession.ExportState() ?? [] : [];
            ResetOutputPlanning();
            session = result.Capture;
            decodeConfiguration = nextConfiguration;
            replaySession = presentation.Replay;
            decodedRows = presentation.DecodedRows;
            decodedRowsBySourceId = presentation.DecodedRowsBySourceId;
            diagnosticRows = presentation.DiagnosticRows;
            replayFrames = result.Workspace.Frames;
            autoPauseIndex = result.Workspace.AutoPauseIndex;
            CreateReviewWorkspace(
                diagnosticRows.Select(row => row.Diagnostic).ToArray(),
                result.Workspace.Frames.Snapshots,
                preservedMarkers,
                preservedReviewState,
                preserveReview ? preservedWorkspaceState : null);
            paintWorkspace = new ReplayPaintWorkspace(
                replaySession,
                presentation.TrailHistory,
                CreateInitialPaintSettings(presentation.Extent, preservedPaintSettings, replaySession.Count),
                reviewWorkspace?.ReviewSession.Diagnostics,
                reviewWorkspace?.Markers);

            DecodedFramesList.ItemsSource = decodedRows;
            SessionStatusText.Text = $"{result.Decode.DisplayIdentity} · {decodedRows.Length:N0} frames · {diagnosticRows.Length:N0} findings";
            ConfigurationHintText.Text = $"{result.Decode.DisplayIdentity} confirmed · decoded automatically · raw source remains unchanged";
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
                var logicalIndex = preserveWorkspaceContext
                    ? Math.Clamp(preservedWorkspaceState?.CurrentLogicalIndex ?? 0, 0, decodedRows.Length - 1)
                    : 0;
                if (preserveWorkspaceContext && preservedWorkspaceTab?.IsEnabled == true)
                    WorkspaceTabs.SelectedItem = preservedWorkspaceTab;
                else
                    WorkspaceTabs.SelectedItem = PaintTab;
                outputWorkspaceActive = ReferenceEquals(WorkspaceTabs.SelectedItem, AnalysisTab);
                var paintSelected = ReferenceEquals(WorkspaceTabs.SelectedItem, PaintTab);
                SetOutputWorkspaceChrome(outputWorkspaceActive, showReplayTransport: paintSelected);
                SetSourceTransportEnabled(paintSelected && !outputWorkspaceActive);
                SeekReplay(logicalIndex);
                if (preserveWorkspaceContext && preservedWorkspaceState?.SelectedContactId is { } contactId)
                {
                    reviewWorkspace?.SelectContact(contactId);
                    if (replayFrames is { } frames) PresentReplayDetails(frames[logicalIndex]);
                }
                RefreshOutputPreviewIfVisible();
            }
            else
            {
                WorkspaceTabs.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            if (IsActiveCaptureDecode(operationGeneration, decodeCancellation))
                SessionStatusText.Text = "Decode cancelled; previous complete result was preserved";
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            if (IsActiveCaptureDecode(operationGeneration, decodeCancellation))
            {
                SessionStatusText.Text = $"Decode failed · {exception.Message}";
                ConfigurationHintText.Text = "Raw records remain available; verify version/profile and try again.";
            }
        }
        finally
        {
            if (IsActiveCaptureDecode(operationGeneration, decodeCancellation))
            {
                activeCaptureDecodeGeneration = 0;
                SetBusy(false, SessionStatusText.Text ?? "Ready");
            }
        }
    }

    private bool IsActiveCaptureLoad(long generation, CancellationTokenSource cancellation) =>
        activeCaptureLoadGeneration == generation && ReferenceEquals(operationCancellation, cancellation);

    private bool IsActiveCaptureDecode(long generation, CancellationTokenSource cancellation) =>
        ReferenceEquals(operationCancellation, cancellation) &&
        (generation == 0 || activeCaptureDecodeGeneration == generation);

    private ReviewOperationIdentity CaptureReviewOperationIdentity()
    {
        var loadedSession = session ?? throw new InvalidOperationException("A loaded capture is required.");
        return new ReviewOperationIdentity(
            loadedSession,
            reviewWorkspace,
            decodeConfiguration,
            loadedSession.SourceSha256,
            activeCaptureLoadGeneration,
            activeCaptureDecodeGeneration);
    }

    private bool IsCurrentReviewOperation(ReviewOperationIdentity identity) =>
        ReferenceEquals(session, identity.Session) &&
        ReferenceEquals(reviewWorkspace, identity.Workspace) &&
        decodeConfiguration == identity.DecodeConfiguration &&
        string.Equals(session?.SourceSha256, identity.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
        activeCaptureLoadGeneration == identity.LoadGeneration &&
        activeCaptureDecodeGeneration == identity.DecodeGeneration;

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
        captureDecodeController.CancelCurrent();
    }

    private void OutputContentComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutputContentComboBox?.SelectedItem is not SelectOption option ||
            OutputVideoPanel is null || OutputHeatmapPanel is null || OutputPackagePanel is null)
            return;

        var mp4 = option.Value == "mp4";
        var heatmap = option.Value == "heatmap";
        var outputTypeChanged = outputWorkspace.Update(outputWorkspace.Settings with
        {
            ContentType = option.Value switch
            {
                "heatmap" => ReplayOutputContentType.Heatmap,
                "package" => ReplayOutputContentType.DataPackage,
                _ => ReplayOutputContentType.Mp4Video,
            },
        });
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
        if (outputTypeChanged) RefreshOutputPreviewIfVisible();
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
        var operation = CaptureOutputOperation(SelectedOutputRange());
        SetBusy(true, "Rendering heatmap PNG");
        try
        {
            var reportSnapshot = await GetOutputReportAsync(operation.ReportIdentity, cancellationToken);
            ThrowIfStaleOutputOperation(operation);
            var report = reportSnapshot.Report;
            currentOutputReport = report;
            RefreshHeatmapPresentation();
            await WriteCurrentHeatmapPreviewAsync(path, cancellationToken);
            ThrowIfStaleOutputOperation(operation);
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
        if (session is null || replaySession is null || decodeConfiguration is null || reviewWorkspace is null || replaySession.Count == 0) return;
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
        var operation = CaptureOutputOperation(SelectedOutputRange());
        SetBusy(true, "Analyzing selected replay range");
        try
        {
            var reportSnapshot = await GetOutputReportAsync(operation.ReportIdentity, cancellationToken);
            ThrowIfStaleOutputOperation(operation);
            var report = reportSnapshot.Report;
            var range = reportSnapshot.Identity.Range;
            var result = await new AnalysisOutputWriter().WriteAsync(
                directory,
                report,
                cancellationToken,
                reportSnapshot.Inputs.Capture.Records);
            ThrowIfStaleOutputOperation(operation);
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
        CancelOutputExportJob();
    }

    private async Task BeginReplayExportAsync(bool longExportConfirmed)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || replaySession.Count == 0 || outputExportJobs.IsActive) return;
        outputWorkspace.Update(outputWorkspace.Settings with
        {
            ContentType = ReplayOutputContentType.Mp4Video,
            Range = SelectedOutputRange(),
        });
        ReplayOutputPreviewPlan preview;
        try
        {
            outputPreviewPlanPending = true;
            outputPreviewPlanError = null;
            UpdateOutputExportButtonAvailability();
            preview = await outputPlanService.GetPreviewAsync(
                replaySession,
                OutputReplayIdentity(),
                outputWorkspace.Settings,
                outputPreviewCancellation?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "MP4 planning was cancelled; no output was created";
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            outputPreviewPlanError = exception.Message;
            AnalysisSummaryText.Text = "Output preview unavailable";
            OutputHotspotText.Text = exception.Message;
            SessionStatusText.Text = $"MP4 export unavailable · {exception.Message}";
            ClearOutputVideoPreview();
            return;
        }
        finally
        {
            outputPreviewPlanPending = false;
            UpdateOutputExportButtonAvailability();
        }
        if (preview.Identity.Settings != outputWorkspace.Settings)
        {
            SessionStatusText.Text = "Output settings changed; review the refreshed MP4 preview before exporting.";
            RefreshOutputPreviewIfVisible();
            return;
        }
        if (!ReferenceEquals(outputVideoPreviewPlan?.FramePlan, preview.FramePlan) ||
            outputVideoPreviewPlan.Identity != preview.Identity)
            PrepareOutputVideoPreview(preview, PreviewClockName(preview.Identity.Settings), $"{preview.Identity.Settings.Speed:0.##}×");
        var estimate = preview.Estimate;
        var settings = preview.Identity.Settings;
        if (!longExportConfirmed && estimate.RequiresConfirmation)
        {
            OutputExportWarningText.Text =
                $"This selection produces {estimate.OutputFrameCount:N0} frames ({FormatClock(estimate.Duration)}) at " +
                $"{settings.FrameRate} FPS and {settings.Width} × {settings.Height}. " +
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

        var options = CreateReplayExportOptions(path);
        if (paintWorkspace is null)
            throw new InvalidOperationException("A decoded Paint workspace is required for MP4 output.");
        var exportPaintWorkspace = new ReplayPaintWorkspace(
            paintWorkspace.Replay,
            paintWorkspace.TrailHistory,
            paintWorkspace.Settings,
            paintWorkspace.Diagnostics,
            paintWorkspace.Markers);
        var exportReplay = exportPaintWorkspace.Replay;
        ReplayScene ExportScene(int logicalIndex) => exportPaintWorkspace.CreateScene(logicalIndex);

        var handle = StartOutputExportJob(
            (cancellationToken, progress) => new ReplayRangeExporter().ExportAsync(
                exportReplay,
                ExportScene,
                options,
                preview.FramePlan,
                cancellationToken,
                progress));
        var completed = await handle.Completion;
        if (completed.JobId != outputExportJobs.Snapshot.JobId) return;

        PresentOutputExportSnapshot(completed);
        if (completed.Status == ExportJobStatus.Succeeded && completed.Result is { } result)
        {
            AnalysisOutputText.Text =
                $"{result.Kind} · {result.OutputPath}\n{result.OutputFrameCount:N0} frames · {result.Duration.TotalSeconds:0.###} s\nmanifest={result.ManifestPath}" +
                (result.Warning is null ? string.Empty : $"\nWARNING: {result.Warning}");
            SessionStatusText.Text = result.Kind == ReplayExportKind.Mp4
                ? "Replay MP4 exported"
                : "FFmpeg unavailable · PNG sequence exported safely";
        }
        else if (completed.Status == ExportJobStatus.Cancelled)
        {
            SessionStatusText.Text = "Replay export cancelled; no partial output was kept";
        }
        else if (completed.Status == ExportJobStatus.Failed)
        {
            SessionStatusText.Text = $"Replay export failed · {completed.Error?.Message ?? "Unknown export error"}";
        }
    }

    internal static bool RequiresLongVideoExport(int frameCount, TimeSpan duration) =>
        frameCount >= ReplayOutputWorkspace.LongExportFrameThreshold ||
        duration >= ReplayOutputWorkspace.LongExportDurationThreshold;

    internal ExportJobHandle StartOutputExportJob(
        Func<CancellationToken, IProgress<ReplayExportProgress>, Task<ReplayExportResult>> operation)
    {
        var observer = new Progress<ExportJobSnapshot>(PresentOutputExportSnapshot);
        var handle = outputExportJobs.Start(operation, observer);
        PresentOutputExportSnapshot(outputExportJobs.Snapshot);
        return handle;
    }

    internal bool CancelOutputExportJob()
    {
        var cancelled = outputExportJobs.Cancel();
        PresentOutputExportSnapshot(outputExportJobs.Snapshot);
        return cancelled;
    }

    internal void PresentOutputExportSnapshot(ExportJobSnapshot snapshot)
    {
        var authoritative = outputExportJobs.Snapshot;
        if (snapshot.JobId != authoritative.JobId) return;
        snapshot = authoritative;
        var exporting = snapshot.IsActive;
        LoadButton.IsEnabled = !exporting && !operationInProgress;
        OutputExportActivityPanel.IsVisible = exporting;
        OutputExportCancelButton.IsEnabled = snapshot.Status == ExportJobStatus.Running;
        if (exporting)
        {
            var progress = snapshot.Progress;
            OutputExportProgressBar.IsIndeterminate = progress is null || progress.TotalFrames == 0;
            OutputExportProgressBar.Value = progress?.Percent ?? 0;
            OutputExportStatusText.Text = snapshot.Status == ExportJobStatus.Cancelling
                ? "Cancelling MP4 export…"
                : progress?.Stage ?? "Preparing MP4 export";
            OutputExportProgressText.Text = progress is { TotalFrames: > 0 }
                ? $"{progress.Percent:0}% · {progress.CompletedFrames:N0}/{progress.TotalFrames:N0}"
                : "Preparing";
        }
        UpdateOutputExportButtonAvailability();
    }

    private void UpdateOutputExportButtonAvailability()
    {
        if (ExportSelectedOutputButton is null) return;
        var mp4Selected = (OutputContentComboBox.SelectedItem as SelectOption)?.Value == "mp4";
        var previewReady = !outputPreviewPlanPending && outputPreviewPlanError is null &&
                           replaySession is not null &&
                           MatchingOutputVideoPreviewPlan(outputWorkspace.Settings, OutputReplayIdentityOrEmpty()) is not null;
        ExportSelectedOutputButton.IsEnabled =
            !operationInProgress && replaySession is { Count: > 0 } &&
            (!mp4Selected || !outputExportJobs.IsActive && previewReady);
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

        var clock = SelectedTag(OutputClockComboBox) == "recorded"
            ? ReplayExportClock.Recorded
            : ReplayExportClock.Frame;
        OutputClockDescriptionText.Text = clock == ReplayExportClock.Recorded
            ? "Follows source timestamps, including long idle gaps with no touch."
            : "Plays every decoded frame at 120 Hz; REC labels keep their original timestamps.";

        var speedTag = SelectedTag(OutputSpeedComboBox);
        OutputCustomSpeedTextBox.IsVisible = speedTag == "custom";
        var frameRateTag = SelectedTag(OutputFrameRateComboBox);
        OutputCustomFrameRateTextBox.IsVisible = frameRateTag == "custom";

        var resolutionTag = SelectedTag(OutputResolutionComboBox);
        var customResolution = resolutionTag == "custom";
        OutputCustomResolutionLabel.IsVisible = customResolution;
        OutputCustomResolutionPanel.IsVisible = customResolution;
        if (resolutionTag == "panel")
        {
            SyncOutputResolutionWithPanel(updateWorkspace: false);
        }
        else if (!customResolution && TryParseOutputResolution(resolutionTag, out var width, out var height))
        {
            OutputWidthTextBox.Text = width.ToString(CultureInfo.InvariantCulture);
            OutputHeightTextBox.Text = height.ToString(CultureInfo.InvariantCulture);
        }
        ApplyOutputSettingsFromControls(showStatus: false);
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
        ApplyOutputSettingsFromControls(showStatus: true);
    }

    private void ApplyOutputSettingsFromControls(bool showStatus)
    {
        if (!TryReadOutputSettings(out var settings, out var error))
        {
            SessionStatusText.Text = error;
            return;
        }
        if (!outputWorkspace.Update(settings)) return;

        OutputExportWarningPanel.IsVisible = false;
        if (showStatus)
            SessionStatusText.Text = $"MP4 preview settings · {OutputVideoSettingsLabel()}";
        StopOutputVideoPreviewPlayback();
        RefreshOutputPreviewIfVisible();
    }

    private bool TryReadOutputSettings(out ReplayOutputSettings settings, out string error)
    {
        settings = outputWorkspace.Settings;
        error = string.Empty;

        var speedTag = SelectedTag(OutputSpeedComboBox);
        if (!double.TryParse(
                speedTag == "custom" ? OutputCustomSpeedTextBox.Text : speedTag,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var speed))
        {
            error = "MP4 speed must be a number greater than 0 and no more than 100×.";
            return false;
        }

        var frameRateTag = SelectedTag(OutputFrameRateComboBox);
        if (!int.TryParse(
                frameRateTag == "custom" ? OutputCustomFrameRateTextBox.Text : frameRateTag,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var frameRate))
        {
            error = $"MP4 frame rate must be between 1 and {ReplayFramePlan.MaximumFrameRate} FPS.";
            return false;
        }

        var width = settings.Width;
        var height = settings.Height;
        var resolutionTag = SelectedTag(OutputResolutionComboBox);
        if (resolutionTag == "panel")
        {
            width = NormalizeVideoDimension(CurrentPaintExtent.MaximumX, 320, ReplayOutputWorkspace.MaximumWidth);
            height = NormalizeVideoDimension(CurrentPaintExtent.MaximumY, 180, ReplayOutputWorkspace.MaximumHeight);
        }
        else if (resolutionTag == "custom")
        {
            if (!int.TryParse(OutputWidthTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
                !int.TryParse(OutputHeightTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            {
                error = $"MP4 size must use even values: width 320-{ReplayOutputWorkspace.MaximumWidth} and height 180-{ReplayOutputWorkspace.MaximumHeight}.";
                return false;
            }
        }
        else if (!TryParseOutputResolution(resolutionTag, out width, out height))
        {
            error = "Select a valid MP4 resolution.";
            return false;
        }

        settings = settings with
        {
            Clock = SelectedTag(OutputClockComboBox) == "recorded" ? ReplayExportClock.Recorded : ReplayExportClock.Frame,
            Speed = speed,
            FrameRate = frameRate,
            Width = width,
            Height = height,
            Range = SelectedOutputRange(),
        };
        var validation = new ReplayOutputWorkspace(settings with
        {
            ContentType = ReplayOutputContentType.Mp4Video,
        }).Validate(replaySession);
        if (validation.IsValid) return true;

        error = validation.Errors[0];
        return false;
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

    private bool SyncOutputResolutionWithPanel(bool updateWorkspace = true)
    {
        var width = NormalizeVideoDimension(CurrentPaintExtent.MaximumX, 320, ReplayOutputWorkspace.MaximumWidth);
        var height = NormalizeVideoDimension(CurrentPaintExtent.MaximumY, 180, ReplayOutputWorkspace.MaximumHeight);
        OutputPanelResolutionItem.Content = $"Paint · {width} × {height}";
        if (OutputResolutionComboBox.SelectedItem is not ComboBoxItem item || item.Tag?.ToString() != "panel")
            return false;
        OutputWidthTextBox.Text = width.ToString(CultureInfo.InvariantCulture);
        OutputHeightTextBox.Text = height.ToString(CultureInfo.InvariantCulture);
        return updateWorkspace && outputWorkspace.Update(outputWorkspace.Settings with { Width = width, Height = height });
    }

    private static int NormalizeVideoDimension(double value, int minimum, int maximum)
    {
        var result = (int)Math.Clamp(Math.Round(value), minimum, maximum);
        return result % 2 == 0 ? result : Math.Min(maximum, result + 1);
    }

    private string OutputVideoSettingsLabel() =>
        $"{outputWorkspace.Settings.Width} × {outputWorkspace.Settings.Height} / {outputWorkspace.Settings.FrameRate} FPS / {outputWorkspace.Settings.Speed:0.##}×";

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

    private AnalysisRange SelectedOutputRange()
    {
        if (replaySession is null || replaySession.Count == 0)
            return new AnalysisRange(0, 0);
        return loopIn is { } start && loopOut is { } end
            ? new AnalysisRange(Math.Min(start, end), Math.Max(start, end))
            : new AnalysisRange(0, replaySession.Count - 1);
    }

    private ReplayExportOptions CreateReplayExportOptions(string path)
    {
        if (session is null || decodeConfiguration is null)
            throw new InvalidOperationException("A decoded capture is required for MP4 output.");
        return outputWorkspace.CreateExportOptions(
            path,
            CurrentPaintMode,
            sourceFileName: Path.GetFileName(session.SourcePath),
            sourceSha256: session.SourceSha256,
            decodeConfiguration: decodeConfiguration,
            renderSettings: CurrentReplayRenderSettings());
    }

    internal ReplayExportOptions CreateCurrentOutputExportOptions(string path) => CreateReplayExportOptions(path);

    private ReplayOutputPreviewPlan? MatchingOutputVideoPreviewPlan(
        ReplayOutputSettings settings,
        string replayIdentity) =>
        replaySession is not null &&
        outputVideoPreviewPlan is { } current &&
        ReferenceEquals(outputVideoPreviewReplay, replaySession) &&
        current.Identity.ReplayIdentity == replayIdentity &&
        current.Identity.ReplayFrameCount == replaySession.Count &&
        current.Identity.ReplayFrameInterval == replaySession.FrameInterval &&
        current.Identity.Settings == settings
            ? current
            : null;

    private string OutputReplayIdentity()
    {
        if (session is null || decodeConfiguration is null)
            throw new InvalidOperationException("A decoded replay is required for output preview.");
        return string.Join(
            '/',
            session.SourceSha256,
            decodeConfiguration.SourceAdapterId,
            $"0x{decodeConfiguration.EventBufferBase:X8}",
            decodeConfiguration.EventBufferVersion,
            decodeConfiguration.Desay97Profile ?? string.Empty,
            decodeConfiguration.RegisterProfile ?? string.Empty);
    }

    private string OutputReplayIdentityOrEmpty() =>
        session is null || decodeConfiguration is null ? string.Empty : OutputReplayIdentity();

    private static string PreviewClockName(ReplayOutputSettings settings) =>
        settings.Clock == ReplayExportClock.Frame ? "Frame-paced 120 Hz" : "Recorded gaps";

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

    private OutputReportInputs CaptureOutputReportInputs(AnalysisRange range)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewWorkspace is null)
            throw new InvalidOperationException("A decoded replay is required for output preview.");
        var evidence = decodeConfiguration.EventBufferVersion is "0x83" or "0x84"
            ? EvidenceStatus.Verified
            : EvidenceStatus.Provisional;
        return OutputReportInputs.CaptureCurrent(
            session,
            replaySession,
            decodeConfiguration,
            evidence,
            range,
            replayFrames,
            reviewWorkspace);
    }

    private OutputReportIdentity CaptureOutputReportIdentity(
        AnalysisRange range,
        string replayIdentity)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewWorkspace is null)
            throw new InvalidOperationException("A decoded replay is required for output preview.");
        return new OutputReportIdentity(
            session,
            replaySession,
            decodeConfiguration,
            replayIdentity,
            range,
            reviewWorkspace.ReportRevision);
    }

    private Task<OutputReportSnapshot> GetOutputReportAsync(
        AnalysisRange range,
        string replayIdentity,
        CancellationToken cancellationToken)
    {
        var identity = CaptureOutputReportIdentity(range, replayIdentity);
        return GetOutputReportAsync(identity, cancellationToken);
    }

    private Task<OutputReportSnapshot> GetOutputReportAsync(
        OutputReportIdentity identity,
        CancellationToken cancellationToken)
    {
        return outputReportService.GetAsync(
            identity,
            () => CaptureOutputReportInputs(identity.Range),
            cancellationToken);
    }

    private OutputOperationIdentity CaptureOutputOperation(AnalysisRange range) => new(
        CaptureOutputReportIdentity(range, OutputReplayIdentity()),
        activeCaptureLoadGeneration,
        activeCaptureDecodeGeneration);

    private bool IsCurrentOutputOperation(OutputOperationIdentity operation)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewWorkspace is null)
            return false;
        var current = CaptureOutputReportIdentity(operation.ReportIdentity.Range, OutputReplayIdentity());
        return operation.ReportIdentity.Matches(current) &&
               operation.LoadGeneration == activeCaptureLoadGeneration &&
               operation.DecodeGeneration == activeCaptureDecodeGeneration;
    }

    private void ThrowIfStaleOutputOperation(OutputOperationIdentity operation)
    {
        if (!IsCurrentOutputOperation(operation))
            throw new OperationCanceledException("Output analysis was superseded by a newer capture or review generation.");
    }

    internal Task<OutputReportSnapshot> GetCurrentOutputReportForTestingAsync(
        CancellationToken cancellationToken = default) =>
        GetOutputReportAsync(SelectedOutputRange(), OutputReplayIdentity(), cancellationToken);

    private async Task RefreshOutputPreviewAsync()
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewWorkspace is null || replaySession.Count == 0)
            return;

        StopOutputVideoPreviewPlayback();
        outputPreviewCancellation?.Cancel();
        outputPreviewCancellation?.Dispose();
        outputPreviewCancellation = new CancellationTokenSource();
        var cancellationToken = outputPreviewCancellation.Token;
        var generation = checked(++outputPreviewGeneration);
        var range = SelectedOutputRange();
        outputWorkspace.Update(outputWorkspace.Settings with { Range = range });
        var settings = outputWorkspace.Settings;
        var replayIdentity = OutputReplayIdentity();
        AnalysisSummaryText.Text = $"Preparing frames {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}...";
        OutputHotspotText.Text = "Analyzing coordinates";
        outputPreviewPlanPending = settings.ContentType == ReplayOutputContentType.Mp4Video;
        outputPreviewPlanError = null;
        UpdateOutputExportButtonAvailability();
        try
        {
            ReplayOutputPreviewPlan? preview = null;
            if (settings.ContentType == ReplayOutputContentType.Mp4Video)
            {
                preview = await outputPlanService.GetPreviewAsync(
                    replaySession,
                    replayIdentity,
                    settings,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != outputPreviewGeneration || settings != outputWorkspace.Settings) return;
            }
            var reportSnapshot = await GetOutputReportAsync(range, replayIdentity, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != outputPreviewGeneration || settings != outputWorkspace.Settings) return;
            ApplyOutputPreview(reportSnapshot.Report, preview);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            if (generation != outputPreviewGeneration) return;
            currentOutputReport = null;
            outputPreviewPlanError = exception.Message;
            AnalysisSummaryText.Text = "Output preview unavailable";
            OutputHotspotText.Text = exception.Message;
            AnalysisHeatmapPreview.Show(null);
            ClearOutputVideoPreview();
        }
        finally
        {
            if (generation == outputPreviewGeneration)
            {
                outputPreviewPlanPending = false;
                UpdateOutputExportButtonAvailability();
            }
        }
    }

    private void ApplyOutputPreview(CaptureAnalysisReport report, ReplayOutputPreviewPlan? preview)
    {
        if (session is null || decodeConfiguration is null) return;
        currentOutputReport = report;
        var range = report.Manifest.Range;
        var settings = outputWorkspace.Settings;
        var clockName = PreviewClockName(settings);
        var profile = string.IsNullOrWhiteSpace(decodeConfiguration.Desay97Profile)
            ? string.Empty
            : $" / {decodeConfiguration.Desay97Profile}";
        var speed = $"{settings.Speed:0.##}×";

        AnalysisSummaryText.Text = $"Previewing frames {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}";
        OutputRangeText.Text = $"{report.Events.Count:N0} frames\n{range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}";
        OutputFindingsText.Text = $"{report.DiagnosticAggregates.Count:N0} groups\n{report.Asil.UnresolvedAlarmGroups:N0} alarms";
        OutputConfigurationText.Text =
            $"decoder  {decodeConfiguration.EventBufferVersion}{profile}\n" +
            $"source   {session.Probe.DisplayName}\n" +
            $"evidence {report.Manifest.FormatEvidenceStatus}\n" +
            $"paint    {CurrentPaintMode}\n" +
            $"replay   {clockName} at {speed}";
        OutputSourceText.Text =
            $"{Path.GetFileName(session.SourcePath)}\nSHA-256\n{FormatSha256(session.SourceSha256)}";
        RefreshHeatmapPresentation();
        if (preview is not null)
            PrepareOutputVideoPreview(preview, clockName, speed);
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

    private void PrepareOutputVideoPreview(ReplayOutputPreviewPlan preview, string clockName, string speed)
    {
        if (replaySession is null) return;
        var framePlanChanged = !ReferenceEquals(outputVideoPreviewPlan?.FramePlan, preview.FramePlan);
        outputVideoPreviewPlan = preview;
        outputVideoPreviewReplay = replaySession;
        outputVideoFrameIndex = framePlanChanged
            ? 0
            : Math.Clamp(outputVideoFrameIndex, 0, Math.Max(0, preview.Estimate.OutputFrameCount - 1));
        var settings = preview.Identity.Settings;
        var duration = preview.Estimate.Duration;
        OutputClockText.Text = $"{FormatClock(duration)}\n{outputVideoFrameCount:N0} frames at {settings.FrameRate} FPS";
        OutputVideoBadgeText.Text = OutputVideoSettingsLabel();
        OutputVideoTimeline.SetFrameCount(outputVideoFrameCount, settings.FrameRate);
        OutputVideoTimeline.SetSourceRange(replaySession.Count, settings.Range.StartLogicalIndex, settings.Range.EndLogicalIndex);
        OutputFullscreenTimeline.SetFrameCount(outputVideoFrameCount, settings.FrameRate);
        OutputPreviewPlayPauseButton.IsEnabled = outputVideoFrameCount > 0;
        OutputFullscreenPlayPauseButton.IsEnabled = outputVideoFrameCount > 0;
        OutputPreviewFullscreenButton.IsEnabled = outputVideoFrameCount > 0;
        outputVideoPreviewDirty = true;
        ShowOutputVideoFrame(outputVideoFrameIndex);
        OutputConfigurationText.Text += $"\nvideo    {clockName} / {settings.FrameRate} FPS / {speed}";
    }

    private void ShowOutputVideoFrame(int outputFrameIndex)
    {
        if (replaySession is null || outputVideoFrameCount == 0 || outputVideoPreviewPlan is null ||
            !ReferenceEquals(outputVideoPreviewReplay, replaySession)) return;
        outputVideoFrameIndex = Math.Clamp(outputFrameIndex, 0, outputVideoFrameCount - 1);
        var logicalIndex = ReplayFramePlan.LogicalIndexAt(outputVideoPreviewPlan.Entries, outputVideoFrameIndex);
        var scene = CreateReplayScene(logicalIndex);
        var settings = CurrentReplayRenderSettings();
        var outputSettings = outputVideoPreviewPlan.Identity.Settings;
        OutputVideoPreview.Show(scene, settings, outputSettings.Width, outputSettings.Height);
        if (outputFullscreenActive)
            OutputFullscreenVideoPreview.Show(scene, settings, outputSettings.Width, outputSettings.Height);
        outputVideoPreviewDirty = false;
        OutputVideoTimeline.SetPosition(outputVideoFrameIndex);
        OutputFullscreenTimeline.SetPosition(outputVideoFrameIndex);
        var currentTime = TimeSpan.FromSeconds(outputVideoFrameIndex / (double)outputSettings.FrameRate);
        var duration = outputVideoPreviewPlan.Estimate.Duration;
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
        ApplyLoopConfiguration(loopEnabled, e.StartLogicalIndex, e.EndLogicalIndex);
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
        outputVideoPreviewPlan = null;
        outputVideoPreviewReplay = null;
        outputVideoFrameIndex = 0;
        outputVideoPreviewDirty = false;
        OutputVideoBadgeText.Text = outputPreviewPlanError ?? "Preview unavailable";
        OutputClockText.Text = "-";
        OutputVideoPreview.Clear();
        OutputFullscreenVideoPreview.Clear();
        OutputVideoTimeline.SetFrameCount(0, outputVideoFrameRate);
        OutputFullscreenTimeline.SetFrameCount(0, outputVideoFrameRate);
        UpdateOutputExportButtonAvailability();
        OutputPreviewPlayPauseButton.IsEnabled = false;
        OutputFullscreenPlayPauseButton.IsEnabled = false;
        OutputPreviewFullscreenButton.IsEnabled = false;
        OutputPreviewClockText.Text = "00:00.000 / 00:00.000";
        OutputPreviewFrameText.Text = "output 0/0";
        OutputFullscreenClockText.Text = "00:00.000 / 00:00.000";
        OutputFullscreenFrameText.Text = "0 / 0";
    }

    private void InvalidateCurrentOutputVideoFrame()
    {
        outputVideoPreviewDirty = true;
        RefreshCurrentOutputVideoFrame();
    }

    private void RefreshCurrentOutputVideoFrame()
    {
        var visible = outputFullscreenActive || outputWorkspaceActive && OutputVideoPanel.IsVisible;
        if (!visible || !outputVideoPreviewDirty || outputVideoFrameCount == 0 || outputVideoPreviewPlan is null)
            return;
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
        var operationIdentity = CaptureReviewOperationIdentity();
        var preservedWorkspaceState = CaptureReviewWorkspaceState();
        var preservedWorkspaceTab = WorkspaceTabs.SelectedItem as TabItem;
        CaptureSession? profiledSession = null;
        var committed = false;
        SetBusy(true, choice.IcFamily is null ? "Removing IC register profile" : $"Applying IC profile {choice.IcFamily}");
        try
        {
            profiledSession = await Task.Run(() => loadedSession.WithRegisterProfile(choice.IcFamily));
            var projected = await Task.Run(() =>
                RegisterActivityProjector.Project(profiledSession.Records, profiledSession.RegisterAnnotations).ToArray());
            if (!IsCurrentReviewOperation(operationIdentity)) return;

            session = profiledSession;
            ApplyRawExplorerProjection(projected, selectedId);
            var diagnostics = SessionDiagnostics(session);
            diagnosticRows = diagnostics.Select(diagnostic => new DiagnosticRow(diagnostic)).ToArray();
            CreateReviewWorkspace(diagnostics, workspaceState: preservedWorkspaceState);
            SynchronizeReviewWorkspaceConsumers();
            committed = true;
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
            var stillOwnsUi = committed
                ? ReferenceEquals(session, profiledSession) &&
                  activeCaptureLoadGeneration == operationIdentity.LoadGeneration &&
                  activeCaptureDecodeGeneration == operationIdentity.DecodeGeneration
                : IsCurrentReviewOperation(operationIdentity);
            if (stillOwnsUi) SetBusy(false, SessionStatusText.Text ?? "Ready");
        }

        if (!committed) return;
        var canDecode = EventVersionComboBox.SelectedIndex >= 0 &&
            (EventVersionComboBox.SelectedIndex != 4 ||
             (choice.IcFamily is not null && Desay97ProfileComboBox.SelectedIndex >= 0));
        if (canDecode)
            await DecodeSelectedAsync(
                preserveWorkspaceContext: true,
                preservedWorkspaceStateOverride: preservedWorkspaceState,
                preservedWorkspaceTabOverride: preservedWorkspaceTab);
    }

    internal async Task ApplyRegisterProfileForTestingAsync(string? icFamily)
    {
        var choice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
            .FirstOrDefault(item => string.Equals(item.IcFamily, icFamily, StringComparison.OrdinalIgnoreCase));
        if (choice is null && icFamily is null)
            choice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
                .FirstOrDefault(item => item.IcFamily is null);
        if (choice is null) throw new ArgumentException($"Unknown IC profile '{icFamily}'.", nameof(icFamily));
        await ApplyRegisterProfileAsync(choice);
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
            var result = await new ReadableCommunicationLogWriter().WriteAsync(
                directory,
                session.Records,
                session.RegisterAnnotations,
                cancellationToken);
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
        if (synchronizingReviewSelection) return;
        if (DiagnosticListBox.SelectedItem is not ReviewGroupRow row)
        {
            reviewWorkspace?.ClearFindingSelection();
            ClearReviewSelectionPresentation();
            return;
        }
        if (reviewWorkspace is null) return;
        var occurrence = reviewWorkspace.SelectFinding(row.Group.Id);
        PresentReviewSelection(row.Group, occurrence.Id);
        NavigateToOccurrence(occurrence);
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
        var matchingMarkers = reviewWorkspace?.Markers.Where(marker => marker.Id == row.MarkerId).ToArray() ?? [];
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
        var matchingMarkers = (reviewWorkspace?.Markers ?? [])
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
        if (synchronizingReviewSelection || reviewWorkspace is null ||
            ReviewOccurrenceComboBox.SelectedItem is not ReviewOccurrenceRow row)
            return;
        var occurrence = reviewWorkspace.SelectOccurrence(row.Number - 1);
        NavigateToOccurrence(occurrence);
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
            reviewWorkspace?.SetFilter(filter);
            RefreshReviewQueue();
        }
    }

    private void AcknowledgeReviewButton_OnClick(object? sender, RoutedEventArgs e) =>
        MutateReview(groupId => reviewWorkspace!.AcknowledgeFinding(groupId));

    private void DispositionReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } tag } && Enum.TryParse<ReviewDisposition>(tag.ToString(), out var disposition))
            MutateReview(groupId => reviewWorkspace!.SetFindingDisposition(groupId, disposition));
    }

    private void ResolveReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            MutateReview(groupId => reviewWorkspace!.ResolveFinding(groupId));
        }
        catch (InvalidOperationException exception)
        {
            ReviewStateText.Text = $"Cannot resolve · {exception.Message}";
        }
    }

    private void MutateReview(Func<string, ReviewEventGroup> action)
    {
        if (reviewWorkspace?.SelectedFindingGroupId is not { } groupId) return;
        var updated = action(groupId);
        RefreshReviewQueue();
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

    private void PresentReviewSelection(ReviewEventGroup group, string? occurrenceId)
    {
        var marker = reviewWorkspace?.SelectedMarker;
        AnnotationMetadataPanel.IsVisible = marker is not null;
        MarkerNameTextBox.Text = marker?.Label ?? string.Empty;
        MarkerQaCaseTextBox.Text = marker is null
            ? string.Empty
            : string.Join(", ", marker.QaCaseIds ?? []);
        ReviewActionsPanel.IsVisible = true;
        ReviewEmptyText.IsVisible = false;
        var occurrences = group.Occurrences
            .Select((occurrence, index) => new ReviewOccurrenceRow(index + 1, occurrence))
            .ToArray();
        synchronizingReviewSelection = true;
        try
        {
            ReviewOccurrenceComboBox.ItemsSource = occurrences;
            ReviewOccurrenceComboBox.SelectedItem = occurrences.FirstOrDefault(row =>
                row.Occurrence.Id.Equals(occurrenceId, StringComparison.Ordinal)) ?? occurrences.FirstOrDefault();
        }
        finally
        {
            synchronizingReviewSelection = false;
        }
        ComboBoxAutoSizer.Fit(ReviewOccurrenceComboBox);
        UpdateReviewState(group);
    }

    private void ClearReviewSelectionPresentation()
    {
        ReviewActionsPanel.IsVisible = false;
        ReviewEmptyText.IsVisible = true;
        AnnotationMetadataPanel.IsVisible = false;
        MarkerNameTextBox.Text = string.Empty;
        MarkerQaCaseTextBox.Text = string.Empty;
        ReviewOccurrenceComboBox.ItemsSource = null;
    }

    private void SynchronizeReviewSelectionFromWorkspace(bool scrollIntoView)
    {
        if (reviewWorkspace?.SelectedFindingGroupId is not { } groupId)
        {
            ClearReviewSelectionPresentation();
            return;
        }
        var row = reviewRows.FirstOrDefault(item => item.Group.Id.Equals(groupId, StringComparison.Ordinal));
        if (row is null)
        {
            ClearReviewSelectionPresentation();
            return;
        }
        synchronizingReviewSelection = true;
        try
        {
            DiagnosticListBox.SelectedItem = row;
            if (scrollIntoView) DiagnosticListBox.ScrollIntoView(row);
        }
        finally
        {
            synchronizingReviewSelection = false;
        }
        PresentReviewSelection(row.Group, reviewWorkspace.SelectedOccurrenceId);
    }

    private void CreateReviewWorkspace(
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyList<ITouchReplaySnapshot>? frames = null,
        IReadOnlyList<ReplayMarker>? replacementMarkers = null,
        IReadOnlyList<ReviewStateSnapshot>? reviewState = null,
        ReviewWorkspaceState? workspaceState = null)
    {
        workspaceState ??= CaptureReviewWorkspaceState();
        var markers = replacementMarkers ?? reviewWorkspace?.Markers.ToArray() ?? [];
        var state = reviewState ?? reviewWorkspace?.ReviewSession.ExportState() ?? [];
        diagnosticLineNumbers = diagnosticRows
            .Select(row => row.Diagnostic.Location.LineNumber)
            .OrderBy(line => line)
            .ToArray();
        reviewWorkspace = new ReviewInspectorWorkspace(
            frames ?? reviewWorkspace?.Frames ?? [],
            diagnostics,
            session?.RegisterAnnotations,
            markers,
            CurrentPaintMode,
            reviewOptions: workspaceState?.ReviewOptions ?? new ReviewSessionOptions(
                    SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail,
                    SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail));
        reviewWorkspace.ReplaceMarkersAndImportState(markers, state);
        RestoreReviewWorkspaceState(reviewWorkspace, workspaceState);
        RefreshReviewQueue();
    }

    private ReviewWorkspaceState? CaptureReviewWorkspaceState() => reviewWorkspace is null
        ? null
        : new ReviewWorkspaceState(
            reviewWorkspace.Filter,
            reviewWorkspace.CurrentLogicalIndex,
            reviewWorkspace.SelectedFindingGroupId,
            reviewWorkspace.SelectedOccurrenceId,
            reviewWorkspace.SelectedMarkerId,
            reviewWorkspace.SelectedContactId,
            reviewWorkspace.ReviewOptions);

    private static void RestoreReviewWorkspaceState(
        ReviewInspectorWorkspace workspace,
        ReviewWorkspaceState? state)
    {
        if (state is null) return;
        workspace.SetReviewOptions(state.ReviewOptions);
        workspace.SetFilter(state.Filter);
        var logicalIndex = workspace.Frames.Count == 0
            ? -1
            : Math.Clamp(state.CurrentLogicalIndex, 0, workspace.Frames.Count - 1);
        if (logicalIndex >= 0) workspace.SelectFrame(logicalIndex);

        var finding = state.SelectedFindingGroupId is { } groupId
            ? workspace.VisibleGroups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.Ordinal))
            : null;
        if (finding is not null)
        {
            var occurrenceIndex = state.SelectedOccurrenceId is { } occurrenceId
                ? finding.Occurrences.ToList().FindIndex(occurrence =>
                    occurrence.Id.Equals(occurrenceId, StringComparison.Ordinal))
                : 0;
            workspace.SelectFinding(finding.Id, Math.Max(0, occurrenceIndex));
        }
        else if (state.SelectedMarkerId is { } markerId)
        {
            workspace.SelectMarker(markerId);
        }

        if (logicalIndex >= 0) workspace.SelectFrame(logicalIndex);
        if (state.SelectedContactId is { } contactId) workspace.SelectContact(contactId);
    }

    private void SynchronizeReviewWorkspaceConsumers()
    {
        RefreshPaintAnnotations();
        RefreshOutputPreviewIfVisible();
        if (reviewWorkspace?.CurrentSnapshot is { } snapshot)
            ShowRecord(snapshot.PrimarySource, snapshot.DecodedFrame, snapshot);
        else if (currentInspectorRecord is not null)
            ShowRecord(currentInspectorRecord, currentInspectorFrame, currentInspectorSnapshot);
    }

    private void RefreshReviewQueue()
    {
        reviewRows = reviewWorkspace?.VisibleGroups.Select(group => new ReviewGroupRow(group)).ToArray() ?? [];
        synchronizingReviewSelection = true;
        try
        {
            DiagnosticListBox.ItemsSource = reviewRows;
            var selected = reviewWorkspace?.SelectedFindingGroupId is { } groupId
                ? reviewRows.FirstOrDefault(row => row.Group.Id == groupId)
                : null;
            DiagnosticListBox.SelectedItem = selected;
        }
        finally
        {
            synchronizingReviewSelection = false;
        }
        DiagnosticCountText.Text = reviewRows.Length.ToString(CultureInfo.InvariantCulture);
        ClearMarkersButton.IsEnabled = reviewWorkspace?.Markers.Count > 0;
        ReplayTimelineSurface.SetMarkerFrames(reviewWorkspace?.Markers.Select(marker => marker.StartLogicalIndex) ?? []);
        if (DiagnosticListBox.SelectedItem is ReviewGroupRow selectedRow)
            PresentReviewSelection(selectedRow.Group, reviewWorkspace?.SelectedOccurrenceId);
        else
            ClearReviewSelectionPresentation();
        if (Bounds.Width > 0) ApplyResponsiveRails(Bounds.Width);
    }

    private void AddMarkerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0 || reviewWorkspace is null) return;
        reviewWorkspace.SelectFrame(currentLogicalIndex);
        var marker = reviewWorkspace.AddMarker();
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = $"Added marker · frame {currentLogicalIndex + 1}";
    }

    private void UnmarkButton_OnClick(object? sender, RoutedEventArgs e) => RemoveMarker(reviewWorkspace?.SelectedMarkerId);

    private void RemoveMarker(string? markerId)
    {
        if (markerId is null || reviewWorkspace?.Markers.FirstOrDefault(marker => marker.Id == markerId) is not { } removed)
            return;
        if (!reviewWorkspace.RemoveMarker(markerId)) return;
        CloseContextMenu(ReplayTimelineSurface);
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = removed.IsRange
            ? $"Removed range marker · frames {removed.StartLogicalIndex + 1}-{removed.EndLogicalIndex + 1}"
            : $"Removed marker · frame {removed.StartLogicalIndex + 1}";
    }

    private void RenameMarkerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMarker(out var marker)) return;
        var label = (MarkerNameTextBox.Text ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            SessionStatusText.Text = "Marker name cannot be empty";
            MarkerNameTextBox.Text = marker.Label;
            return;
        }
        try
        {
            reviewWorkspace!.RenameMarker(marker.Id, label);
            RefreshWorkspaceAfterReviewMutation();
            SessionStatusText.Text = $"Renamed marker · {label}";
        }
        catch (ArgumentException exception)
        {
            SessionStatusText.Text = exception.Message;
            MarkerNameTextBox.Text = marker.Label;
        }
    }

    private void ClearMarkersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (reviewWorkspace is null) return;
        var count = reviewWorkspace.ClearMarkers();
        if (count == 0) return;
        CloseContextMenu(ReplayTimelineSurface);
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = $"Cleared {count} marker{(count == 1 ? string.Empty : "s")}";
    }

    private void ApplyMarkerQaButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMarker(out var marker)) return;
        var qaCaseIds = (MarkerQaCaseTextBox.Text ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        reviewWorkspace!.SetMarkerQaCaseIds(marker.Id, qaCaseIds);
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = $"QA references updated · {qaCaseIds.Length} IDs";
    }

    private bool TryGetSelectedMarker(out ReplayMarker marker)
    {
        marker = reviewWorkspace?.SelectedMarker!;
        return marker is not null;
    }

    private void RefreshWorkspaceAfterReviewMutation()
    {
        RefreshReviewQueue();
        RefreshPaintAnnotations();
        RefreshOutputPreviewIfVisible();
    }

    private object? FindFrame(string sourceId) =>
        decodedRowsBySourceId.GetValueOrDefault(sourceId)?.Frame;

    private void ShowRecord(SourceRecord record, object? frame, ITouchReplaySnapshot? snapshot = null)
    {
        var registerAnnotation = session?.RegisterAnnotations.Find(record.StableId);
        currentInspectorRecord = record;
        currentInspectorFrame = frame;
        currentInspectorSnapshot = snapshot;
        currentInspectorPresentation = snapshot is not null &&
            reviewWorkspace?.CurrentSnapshot?.LogicalIndex == snapshot.LogicalIndex
                ? reviewWorkspace.CurrentPresentation
                : InspectorPresentationBuilder.Build(
                    record,
                    frame,
                    snapshot,
                    replaySession?.Count ?? 0,
                    CurrentPaintMode,
                    registerAnnotation);
        currentInspectorPresentation ??= InspectorPresentationBuilder.Build(
            record,
            frame,
            snapshot,
            replaySession?.Count ?? 0,
            CurrentPaintMode,
            registerAnnotation);
        if (snapshot is not null)
            currentInspectorPresentation = currentInspectorPresentation with
            {
                TimestampLabel = FormatClock(SelectedTime(snapshot.Timeline)),
            };
        ApplyInspectorPresentation(currentInspectorPresentation, frame is null && registerAnnotation is not null);

        var frameAlerts = reviewWorkspace?.ReviewSession.FrameAlerts(record.StableId, record.Location.LineNumber) ?? [];
        var crcFailed = currentInspectorPresentation.CrcFailed;
        var asilAlarm = currentInspectorPresentation.AsilAlarm;
        InspectorAlertBorder.IsVisible = frameAlerts.Count > 0 || crcFailed || asilAlarm;
        InspectorAlertText.Text = frameAlerts.Count > 0
            ? string.Join(" · ", frameAlerts.Select(item => item.Code))
            : crcFailed ? "CRC validation failed" : asilAlarm ? "ASIL alarm active" : string.Empty;
        var hasNavigableFindings = reviewWorkspace?.ReviewSession.HasNavigableFindings == true;
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

    private void SynchronizeVisibleDecodedInspector()
    {
        if (currentInspectorSnapshot is null || reviewWorkspace?.CurrentSnapshot is not { } snapshot ||
            currentInspectorSnapshot.LogicalIndex == snapshot.LogicalIndex)
            return;
        ShowRecord(snapshot.PrimarySource, snapshot.DecodedFrame, snapshot);
    }

    private void ApplyInspectorPresentation(InspectorFramePresentation presentation, bool registerActivity)
    {
        var selectedId = currentInspectorSnapshot is not null
            ? reviewWorkspace?.SelectedContactId
            : (InspectorContactsList.SelectedItem as InspectorContactRow)?.Id;
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

    private string FormatSourceFields(SourceRecord record)
    {
        var fields = session?.RegisterAnnotations.EffectiveFields(record) ?? record.SourceFields;
        return fields is not { Count: > 0 }
            ? "No adapter-specific source fields."
            : string.Join('\n', fields
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(field => $"{field.Key}={field.Value}"));
    }

    private static ReplayDiagnostic[] SessionDiagnostics(CaptureSession capture) =>
        capture.TransportDiagnostics.Concat(capture.RegisterDiagnostics).ToArray();

    private void ResetOutputPlanning()
    {
        checked { outputPreviewGeneration++; }
        outputPreviewCancellation?.Cancel();
        outputPreviewCancellation?.Dispose();
        outputPreviewCancellation = null;
        outputPlanService.Invalidate();
        outputReportService.Reset();
        outputPreviewPlanPending = false;
        outputPreviewPlanError = null;
        currentOutputReport = null;
        AnalysisHeatmapPreview.Show(null);
        ClearOutputVideoPreview();
    }

    private sealed record SourceAdapterChoice(string AdapterId, string DisplayName, ProbeConfidence Confidence)
    {
        public override string ToString() => $"{DisplayName} · {Confidence}";
    }

    private sealed record ReviewWorkspaceState(
        ReviewQueueFilter Filter,
        int CurrentLogicalIndex,
        string? SelectedFindingGroupId,
        string? SelectedOccurrenceId,
        string? SelectedMarkerId,
        byte? SelectedContactId,
        ReviewSessionOptions ReviewOptions);

    private sealed record ReviewOperationIdentity(
        CaptureSession Session,
        ReviewInspectorWorkspace? Workspace,
        ReplayDecodeConfiguration? DecodeConfiguration,
        string SourceSha256,
        long LoadGeneration,
        long DecodeGeneration);

    private void ClearSession()
    {
        StopPlayback();
        ResetOutputPlanning();
        session = null;
        replaySession = null;
        replayFrames = null;
        paintWorkspace = null;
        autoPauseIndex = null;
        allRawRows = [];
        registerActivities = [];
        rawRowsById = [];
        decodedRowsBySourceId = [];
        decodedRows = [];
        diagnosticRows = [];
        diagnosticLineNumbers = [];
        reviewWorkspace = null;
        reviewRows = [];
        ReplayTimelineSurface.SetMarkerFrames([]);
        decodeConfiguration = null;
        pendingSidecar = null;
        playbackController.Clear();
        currentInspectorRecord = null;
        currentInspectorFrame = null;
        currentInspectorSnapshot = null;
        currentInspectorPresentation = null;
        lastDetailPresentationTimestamp = 0;
        DetailPresentationCount = 0;
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
        synchronizingPaintControls = true;
        try
        {
            PaintModeComboBox.SelectedIndex = 0;
            TrailModeComboBox.SelectedIndex = 1;
            TrailLengthComboBox.SelectedIndex = 5;
            TrailLengthComboBox.IsVisible = true;
            TrailLengthLabel.IsVisible = true;
            TraceToggleButton.IsChecked = true;
            TrailPointsToggleButton.IsChecked = true;
            GridStrengthToggleButton.IsChecked = false;
            ReverseXToggleButton.IsChecked = false;
            ReverseYToggleButton.IsChecked = false;
            SwapAxesToggleButton.IsChecked = false;
            LegendPositionComboBox.SelectedIndex = 0;
            LegendVisibleToggleButton.IsChecked = true;
            LegendCompactToggleButton.IsChecked = true;
            PaintSurface.SetMode(ReplayRenderMode.HostState);
            PaintSurface.SetTrailVisibility(showLines: true, showPoints: true);
            PaintSurface.SetStrongGrid(false);
            PaintSurface.SetLegendPosition(ReplayLegendPosition.TopLeft);
            PaintSurface.SetLegendVisible(true);
            PaintSurface.SetLegendCollapsed(true);
        }
        finally
        {
            synchronizingPaintControls = false;
        }
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
        if (replayFrames is null || paintWorkspace is null || autoPauseIndex is null)
            throw new InvalidOperationException("Replay workspace must be prepared before it is presented.");
        playbackController.Load(
            replaySession.Timeline,
            autoPauseIndex,
            (exclusiveStart, inclusiveEnd) => reviewWorkspace?.ReviewSession.FirstPauseIndex(exclusiveStart, inclusiveEnd));
        playbackController.ConfigureAutoPause(new ReplayPlaybackPauseOptions(
            SettingsPage.PlaybackPreferences.PauseOnBreak,
            SettingsPage.PlaybackPreferences.PauseOnAllBreak));
        SynchronizePaintPresentation(fit: true);
        SyncOutputResolutionWithPanel();
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
    }

    private static CaptureDecodePresentation BuildCaptureDecodePresentation(
        CaptureDecodeResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = result.Decode switch
        {
            CommonFormatDecodeResult common => common.Report.Frames
                .Select((frame, index) => DecodedFrameRow.FromCommon(index, frame))
                .ToArray(),
            Desay97FormatDecodeResult desay => desay.Report.Frames
                .Select((frame, index) => DecodedFrameRow.FromDesay97(index, frame))
                .ToArray(),
            _ => throw new InvalidDataException(
                $"Unsupported executable format result '{result.Decode.GetType().Name}'."),
        };
        cancellationToken.ThrowIfCancellationRequested();
        var rowsBySourceId = rows
            .SelectMany(row => row.PhysicalRecords.Append(row.Source).Select(source => (source.StableId, Row: row)))
            .GroupBy(item => item.StableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Row, StringComparer.Ordinal);
        var diagnostics = result.Decode.Diagnostics
            .Select(diagnostic => new DiagnosticRow(diagnostic))
            .ToArray();
        var history = ReplayTrailHistory.Create(result.Workspace.Frames.Snapshots, cancellationToken);
        var extent = new ReplayExtent(
            result.Workspace.Extent.MaximumX,
            result.Workspace.Extent.MaximumY);
        cancellationToken.ThrowIfCancellationRequested();
        return new CaptureDecodePresentation(
            result.Decode.Replay,
            rows,
            rowsBySourceId,
            diagnostics,
            extent,
            history);
    }

    private void PresentCaptureDecodeProgress(CaptureDecodeProgress progress)
    {
        if (progress.Generation != activeCaptureDecodeGeneration) return;
        var phase = progress.Phase switch
        {
            CaptureDecodePhase.SelectingFormat => "Selecting Event Buffer format",
            CaptureDecodePhase.ProjectingRegisters => "Applying IC register profile",
            CaptureDecodePhase.DecodingFrames => "Decoding Event Buffer frames",
            CaptureDecodePhase.BuildingWorkspace => "Building replay workspace",
            CaptureDecodePhase.Ready => "Replay workspace ready",
            _ => progress.Phase.ToString(),
        };
        SessionStatusText.Text = progress.TotalItems is > 0
            ? $"{phase} · {progress.CompletedItems:N0}/{progress.TotalItems:N0}"
            : phase;
    }

    private sealed record CaptureDecodePresentation(
        ITouchReplaySession Replay,
        DecodedFrameRow[] DecodedRows,
        Dictionary<string, DecodedFrameRow> DecodedRowsBySourceId,
        DiagnosticRow[] DiagnosticRows,
        ReplayExtent Extent,
        ReplayTrailHistory TrailHistory);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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

        var clampedIndex = playbackController.Seek(logicalIndex);
        var snapshot = replayFrames?[clampedIndex] ?? replaySession.Seek(clampedIndex);
        reviewWorkspace?.SelectFrame(clampedIndex);
        if (reviewWorkspace?.SelectedContactId is null && InspectorContactsList.SelectedItem is not null)
            InspectorContactsList.SelectedItem = null;
        PaintSurface.SetHighlightedContact(reviewWorkspace?.SelectedContactId);
        RenderReplayFrame(snapshot, crossfade);
        UpdateReplayProgress(snapshot);

        if (synchronizeLists)
            SynchronizeReplayLists(clampedIndex, snapshot);

        if (synchronizeLists || ShouldPresentPlaybackDetails())
            PresentReplayDetails(snapshot);
    }

    private void RenderReplayFrame(ITouchReplaySnapshot snapshot, bool crossfade)
    {
        if (paintWorkspace is null) return;
        PaintSurface.Show(paintWorkspace.CreateScene(snapshot), crossfade);
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
        SeekReplay(playbackController.Step(-1));
    }

    private void NextFrameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        SeekReplay(playbackController.Step(1));
    }

    private void PlayPauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (playbackCancellation is not null)
        {
            StopPlayback();
            return;
        }

        var start = playbackController.Play();
        if (start.Kind == ReplayPlaybackStartKind.Empty)
            return;
        if (start.Kind == ReplayPlaybackStartKind.InvalidLoop)
        {
            TimelineStatusText.Text = "Loop needs at least 2 frames · drag either Loop handle to widen the range";
            return;
        }
        SeekReplay(start.LogicalIndex);
        var cancellation = new CancellationTokenSource();
        playbackCancellation = cancellation;
        SetPlaybackVisualState(playing: true);
        UpdatePlaybackStatus();
        _ = RunPlaybackAsync(cancellation.Token, start.Generation);
    }

    private async Task RunPlaybackAsync(CancellationToken cancellationToken, int generation)
    {
        var playbackStarted = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = playbackController.PlanNext(generation);
                switch (plan.Kind)
                {
                    case ReplayPlaybackOperationKind.Stale:
                        return;
                    case ReplayPlaybackOperationKind.LoopRestart:
                        SeekReplay(plan.LogicalIndex, crossfade: true, synchronizeLists: false);
                        playbackStarted = Stopwatch.GetTimestamp();
                        continue;
                    case ReplayPlaybackOperationKind.Complete:
                        StopPlaybackIfCurrent(generation, "Replay complete · use Home, seek, or Play to restart");
                        return;
                    case ReplayPlaybackOperationKind.InvalidLoop:
                        StopPlaybackIfCurrent(
                            generation,
                            "Loop needs at least 2 frames · drag either Loop handle to widen the range");
                        return;
                }

                await DelayUntilAsync(playbackStarted, plan.DueTime, cancellationToken);
                var operation = playbackController.Advance(
                    generation,
                    Stopwatch.GetElapsedTime(playbackStarted));
                if (operation.Kind == ReplayPlaybackOperationKind.Stale)
                    return;
                if (operation.Kind is ReplayPlaybackOperationKind.Move or ReplayPlaybackOperationKind.Pause)
                    SeekReplay(operation.LogicalIndex, synchronizeLists: false);
                if (operation.Kind == ReplayPlaybackOperationKind.Pause)
                {
                    if (operation.PauseDecision?.SelectReview == true)
                        SelectReviewAt(operation.LogicalIndex);
                    StopPlaybackIfCurrent(
                        generation,
                        $"Paused on {PauseReasonText(operation.PauseDecision)} · continue when ready");
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
        if (generation == playbackController.Generation)
            StopPlayback(status);
    }

    private static string PauseReasonText(ReplayPlaybackPauseDecision? decision) => decision?.Reason switch
    {
        ReplayPlaybackPauseReason.Review => "Alarm / QA Fail",
        ReplayPlaybackPauseReason.AllBreak => "All Break",
        _ => "reported Break",
    };

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
        if (reviewWorkspace is null) return;
        var row = reviewRows.FirstOrDefault(item => item.Group.Occurrences.Any(occurrence =>
            occurrence.LogicalIndex == logicalIndex &&
            (occurrence.Diagnostic.Severity == DiagnosticSeverity.Alarm || occurrence.Category == ReviewEventCategory.QaResult)));
        if (row is null) return;
        DiagnosticListBox.SelectedItem = row;
        DiagnosticListBox.ScrollIntoView(row);
    }

    private TimeSpan DelayBetween(int current, int next)
    {
        return playbackController.Count == 0 ? TimeSpan.Zero : playbackController.DelayBetween(current, next);
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

    private void RestartPlaybackTimingIfActive(ReplayPlaybackConfigurationChange change)
    {
        var previous = playbackCancellation;
        if (!change.RestartRequired || previous is null || replaySession is null || replaySession.Count == 0)
            return;

        var replacement = new CancellationTokenSource();
        playbackCancellation = replacement;
        previous.Cancel();
        previous.Dispose();
        UpdatePlaybackStatus();
        _ = RunPlaybackAsync(replacement.Token, change.Generation);
    }

    private void StopPlayback(string? status = null)
    {
        var cancellation = playbackCancellation;
        playbackCancellation = null;
        playbackController.Pause();
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
        if (synchronizingSelection || replaySession is null) return;

        if (e.IsContinuous)
        {
            pendingContinuousTimelineSeek = e.LogicalIndex;
            if (continuousTimelineSeekScheduled) return;
            continuousTimelineSeekScheduled = true;
            Dispatcher.UIThread.Post(ProcessPendingContinuousTimelineSeek, DispatcherPriority.Render);
            return;
        }

        pendingContinuousTimelineSeek = null;
        StopPlayback();
        SeekReplay(e.LogicalIndex);
    }

    private void ProcessPendingContinuousTimelineSeek()
    {
        continuousTimelineSeekScheduled = false;
        if (pendingContinuousTimelineSeek is not { } logicalIndex || replaySession is null) return;
        pendingContinuousTimelineSeek = null;
        StopPlayback();
        SeekReplay(logicalIndex, synchronizeLists: false);
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
        NotifyPlaybackTimingChanged(nextMaximum
            ? ReplayPlaybackRate.Maximum
            : ReplayPlaybackRate.FromMultiplier(nextSpeed));
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

    private void NotifyPlaybackTimingChanged(ReplayPlaybackRate? selectedRate = null)
    {
        if (ClockModeComboBox is null || CompressIdleCheckBox is null) return;
        var change = playbackController.ConfigureTiming(
            ClockModeComboBox.SelectedIndex == 1
                ? ReplayPlaybackClockDomain.Frame
                : ReplayPlaybackClockDomain.Recorded,
            CompressIdleCheckBox.IsChecked == true,
            selectedRate ?? playbackController.Rate);
        RestartPlaybackTimingIfActive(change);
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

    private void LoopInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0)
        {
            return;
        }
        var nextStart = currentLogicalIndex;
        var nextEnd = loopOut < nextStart ? null : loopOut;
        EnableLoopFromShortcut(nextStart, nextEnd);
        UpdateLoopText();
    }

    private void LoopOutButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0)
        {
            return;
        }
        var nextEnd = currentLogicalIndex;
        var nextStart = loopIn > nextEnd ? null : loopIn;
        EnableLoopFromShortcut(nextStart, nextEnd);
        UpdateLoopText();
    }

    private void LoopToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingLoopControls || ReplayTimelineSurface is null) return;
        var enabled = LoopToggleButton.IsChecked == true;
        var nextStart = loopIn;
        var nextEnd = loopOut;
        if (enabled && replaySession is { Count: > 1 })
        {
            if (nextStart is null || nextEnd is null)
            {
                var range = ReplayLoopRangePlanner.CreateDefault(
                    currentLogicalIndex,
                    replaySession.Count,
                    replaySession.FrameInterval);
                nextStart = range.StartLogicalIndex;
                nextEnd = range.EndLogicalIndex;
            }
        }
        ApplyLoopConfiguration(enabled, nextStart, nextEnd);
        UpdateLoopText();
    }

    private void ReplayTimelineSurface_OnLoopRangeChanged(object? sender, ReplayTimelineLoopEventArgs e)
    {
        ApplyLoopConfiguration(true, e.StartLogicalIndex, e.EndLogicalIndex);
        UpdateLoopText();
    }

    private void EnableLoopFromShortcut(int? start, int? end)
    {
        var enabled = start is not null && end is not null;
        ApplyLoopConfiguration(enabled || loopEnabled, start, end);
        if (!enabled) return;
        synchronizingLoopControls = true;
        try
        {
            LoopToggleButton.IsChecked = true;
        }
        finally
        {
            synchronizingLoopControls = false;
        }
    }

    private void ApplyLoopConfiguration(bool enabled, int? start, int? end)
    {
        var change = playbackController.ConfigureLoop(enabled, start, end);
        RestartPlaybackTimingIfActive(change);
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
        playbackController.ClockDomain == ReplayPlaybackClockDomain.Frame
            ? entry.FrameTime
            : entry.RecordedTime;

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

    private sealed record OutputOperationIdentity(
        OutputReportIdentity ReportIdentity,
        long LoadGeneration,
        long DecodeGeneration);

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
