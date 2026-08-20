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
    private ReplayDecodeConfiguration? decodeConfiguration;


    private readonly CaptureDecodeController captureDecodeController = new();
    private long nextCaptureLoadGeneration;
    private long activeCaptureLoadGeneration;
    private long activeCaptureDecodeGeneration;


    private bool configuringEventVersion;
    private string? pendingSourcePath;
    private bool configuringSourceChoice;
    private bool configuringRegisterProfile;


    private bool operationInProgress;
    private bool outputWorkspaceActive;
    private int themeMode;
    private const double ReplayTransportHeight = 64;
    internal Action<CaptureLoadProgress>? CaptureLoadProgressObserver { get; set; }
    internal Action<CaptureDecodeProgress>? CaptureDecodeProgressObserver { get; set; }
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

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        operationCancellation?.Cancel();
        captureDecodeController.CancelCurrent();
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

    private async void SourceAdapterComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringSourceChoice || pendingSourcePath is not { } path ||
            SourceAdapterComboBox.SelectedItem is not SourceAdapterChoice choice)
            return;
        await OpenCaptureAsync(path, choice.AdapterId);
    }

    private static ReplayDiagnostic[] SessionDiagnostics(CaptureSession capture) =>
        capture.TransportDiagnostics.Concat(capture.RegisterDiagnostics).ToArray();

    private sealed record SourceAdapterChoice(string AdapterId, string DisplayName, ProbeConfidence Confidence)
    {
        public override string ToString() => $"{DisplayName} · {Confidence}";
    }

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
