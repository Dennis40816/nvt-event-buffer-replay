using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private CancellationTokenSource? operationCancellation;
    private CaptureSession? session;
    private ITouchReplaySession? replaySession;
    private RawRecordRow[] allRawRows = [];
    private RegisterActivityEntry[] registerActivities = [];
    private Dictionary<string, RawRecordRow> rawRowsById = [];
    private Dictionary<string, DecodedFrameRow> decodedRowsBySourceId = [];
    private DecodedFrameRow[] decodedRows = [];
    private DiagnosticRow[] diagnosticRows = [];
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
    private IReadOnlyList<ReplayFramePlanEntry> outputVideoPlan = [];
    private int outputVideoFrameCount;
    private int outputVideoFrameIndex;
    private int currentLogicalIndex = -1;
    private int? loopIn;
    private int? loopOut;
    private bool loopEnabled;
    private double replaySpeed = 1;
    private int trailLength = 10;
    private ReplayTrailMode trailMode = ReplayTrailMode.UntilBreak;
    private ReplayLegendPosition legendPosition = ReplayLegendPosition.Auto;
    private ReplayTrailHistory? trailHistory;
    private int trailVisibilityStart;
    private int zoomHintRevision;
    private bool reverseX;
    private bool reverseY;
    private bool maxReplaySpeed;
    private bool synchronizingSelection;
    private bool synchronizingLoopControls;
    private string? pendingSourcePath;
    private bool configuringSourceChoice;
    private bool configuringRegisterProfile;
    private bool operationInProgress;
    private int themeMode;
    private bool reviewRailCollapsed;
    private bool inspectorRailCollapsed;
    private bool? reviewRailUserPreference;
    private bool? inspectorRailUserPreference;
    private const double ExpandedReviewRailWidth = 260;
    private const double ExpandedInspectorRailWidth = 320;
    private const int OutputVideoWidth = 1280;
    private const int OutputVideoHeight = 720;
    private const int OutputVideoFrameRate = 30;
    private readonly Dictionary<TextBlock, IBrush?> originalTextBrushes = [];
    private static readonly HashSet<uint> DarkLiteralTextColors =
    [
        0xFF59646B, 0xFF657078, 0xFF667279, 0xFF68747A, 0xFF69757B, 0xFF707B81,
        0xFF707C82, 0xFF737F85, 0xFF77848A, 0xFF78848A, 0xFF7C878C, 0xFF7D888E,
        0xFF7E898F, 0xFF869197, 0xFF8A969B, 0xFF8D989D, 0xFF8E999E, 0xFF9AA4A8,
        0xFF9FB6BE, 0xFFA6B0B4, 0xFFA8B1B5, 0xFFAAB3B6, 0xFFAEB7BB, 0xFFB8C1C4,
        0xFFC2CACD, 0xFFC4CCCE, 0xFFC8CED0, 0xFFC9D0D2, 0xFFD2D8D6, 0xFFD3D9D7,
        0xFFD8AD62, 0xFFDDE3E1, 0xFFE0E5E3, 0xFFF0F4F2, 0xFFC8F36C, 0xFF58C7D9,
    ];

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
        ShortcutModulesItemsControl.ItemsSource = ReplayShortcutCatalog.Modules;
        ApplyShortcutToolTips();
        PaintSurface.LegendCollapsedChanged += PaintSurface_OnLegendCollapsedChanged;
        RegisterActivitySurface.ActivitySelected += RegisterActivitySurface_OnActivitySelected;
        Opened += (_, _) =>
        {
            ApplyWorkingAreaHeightLimit();
            ScheduleThemeContrast();
            ApplyResponsiveRails(Bounds.Width);
        };
        SizeChanged += MainWindow_OnSizeChanged;
        if (Application.Current is { } application) application.ActualThemeVariantChanged += Application_OnActualThemeVariantChanged;
    }

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsiveRails(e.NewSize.Width);

    private void ApplyWorkingAreaHeightLimit()
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen is null) return;
        const double nonClientAllowance = 40;
        var workingHeight = screen.WorkingArea.Height / screen.Scaling;
        MaxHeight = Math.Max(MinHeight, workingHeight - nonClientAllowance);
        if (Height > MaxHeight) Height = MaxHeight;
    }

    private void ApplyResponsiveRails(double width)
    {
        SetReviewRailCollapsed(reviewRailUserPreference ?? width < 1400);
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

    private void SetReviewRailCollapsed(bool collapsed)
    {
        reviewRailCollapsed = collapsed;
        WorkspaceShellGrid.ColumnDefinitions[0].Width = new GridLength(collapsed ? 42 : ExpandedReviewRailWidth);
        ReviewRailTitle.IsVisible = !collapsed;
        ReviewRailCountBadge.IsVisible = !collapsed;
        ReviewRailContent.IsVisible = !collapsed;
        ReviewRailToggleButton.Content = collapsed ? "\uE76C" : "\uE76B";
        ToolTip.SetTip(ReviewRailToggleButton, collapsed ? "Open review queue" : "Collapse review queue");
    }

    private void SetInspectorRailCollapsed(bool collapsed)
    {
        inspectorRailCollapsed = collapsed;
        WorkspaceShellGrid.ColumnDefinitions[2].Width = new GridLength(collapsed ? 42 : ExpandedInspectorRailWidth);
        InspectorRailTitle.IsVisible = !collapsed;
        InspectorRailContent.IsVisible = !collapsed;
        InspectorRailToggleButton.Content = collapsed ? "\uE76B" : "\uE76C";
        ToolTip.SetTip(InspectorRailToggleButton, collapsed ? "Open inspector" : "Collapse inspector");
    }

    private void Application_OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ScheduleThemeContrast();
        PaintSurface.InvalidateVisual();
    }

    private void ScheduleThemeContrast() => Dispatcher.UIThread.Post(ApplyThemeContrast, DispatcherPriority.Loaded);

    private void ApplyThemeContrast()
    {
        var isLight = Application.Current?.RequestedThemeVariant == ThemeVariant.Light;
        if (!isLight)
        {
            foreach (var pair in originalTextBrushes) pair.Key.Foreground = pair.Value;
            originalTextBrushes.Clear();
            return;
        }
        foreach (var textBlock in this.GetVisualDescendants().OfType<TextBlock>())
        {
            if (textBlock.Foreground is not ISolidColorBrush brush || !DarkLiteralTextColors.Contains(brush.Color.ToUInt32())) continue;
            originalTextBrushes.TryAdd(textBlock, textBlock.Foreground);
            var color = brush.Color.ToUInt32() switch
            {
                0xFFC8F36C => Color.Parse("#537D00"),
                0xFFD8AD62 => Color.Parse("#8A5900"),
                0xFF58C7D9 => Color.Parse("#006B78"),
                var value when ((value >> 16) & 0xff) + ((value >> 8) & 0xff) + (value & 0xff) > 480 => Color.Parse("#17201B"),
                _ => Color.Parse("#536159"),
            };
            textBlock.Foreground = new SolidColorBrush(color);
        }
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
    {
        themeMode = (themeMode + 1) % 2;
        var variant = themeMode == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current is { } application) application.RequestedThemeVariant = variant;
        ToolTip.SetTip(ThemeButton, themeMode == 0 ? "Switch to light theme" : "Switch to dark theme");
        SessionStatusText.Text = $"Color theme · {(themeMode == 0 ? "Dark" : "Light")}";
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
            DecodeButton.IsEnabled = true;
            RegisterProfileComboBox.IsEnabled = true;
            ExportReadableLogButton.IsEnabled = true;
            configuringRegisterProfile = true;
            RegisterProfileComboBox.SelectedIndex = 0;
            configuringRegisterProfile = false;
            ConfigurationHintText.Text = "Confirm the version to decode automatically; source detection never infers it.";
            TimelineSummaryText.Text = $"{session.Records.Count:N0} physical · 0 logical · {diagnosticRows.Length:N0} evidence";
            TimelineStatusText.Text = "Raw capture indexed · confirm Event Buffer Version to open Paint";
            WorkspaceTabs.SelectedIndex = 0;
            if (allRawRows.Length > 0)
            {
                RawRecordsList.SelectedIndex = 0;
            }
            ScheduleThemeContrast();
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

    private async void DecodeButton_OnClick(object? sender, RoutedEventArgs e) => await DecodeSelectedAsync();

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

        EventVersionComboBox.SelectedIndex = versionIndex;
        if (versionIndex == 4)
        {
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
        Desay97Profile? desayProfile = null;
        if (isDesay97)
        {
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
                var report = await Task.Run(() => session.DecodeDesay97(profile), cancellationToken);
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
                NvtRegisterCatalog.FindProfile(session.RegisterProfile)?.EventBufferBase ?? 0x99000,
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
            SessionStatusText.Text = $"{formatLabel} · {decodedRows.Length:N0} decoded frames · {diagnosticRows.Length:N0} diagnostics";
            ConfigurationHintText.Text = $"{formatLabel} confirmed · decoded automatically · raw source remains unchanged";
            TimelineSummaryText.Text = $"{session.Records.Count:N0} physical · {decodedRows.Length:N0} logical · {diagnosticRows.Length:N0} evidence";
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
                WorkspaceTabs.SelectedIndex = 2;
                SeekReplay(0);
            }
            else
            {
                WorkspaceTabs.SelectedIndex = 0;
            }
            ScheduleThemeContrast();
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

    private async void ExportReplayButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || replaySession.Count == 0) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export replay MP4",
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.SourcePath) + ".replay.mp4",
            DefaultExtension = "mp4",
            FileTypeChoices = [new FilePickerFileType("MPEG-4 video") { Patterns = ["*.mp4"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, "Rendering replay export");
        try
        {
            var range = SelectedOutputRange();
            var options = CreateReplayExportOptions(path, range);
            var result = await new ReplayRangeExporter().ExportAsync(
                replaySession,
                CreateReplayScene,
                options,
                cancellationToken);
            AnalysisOutputText.Text =
                $"{result.Kind} · {result.OutputPath}\n{result.OutputFrameCount:N0} frames · {result.Duration.TotalSeconds:0.###} s\nmanifest={result.ManifestPath}" +
                (result.Warning is null ? string.Empty : $"\nWARNING: {result.Warning}");
            WorkspaceTabs.SelectedItem = AnalysisTab;
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
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private void EventVersionComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var isDesay97 = (sender as ComboBox)?.SelectedItem is ComboBoxItem item &&
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
                ? "Confirm Standard or Benz Palm; decoding starts immediately after that selection."
                : "Version confirmed · decoding automatically; source detection did not infer it.";
        }
    }

    private void OpenOutputPreviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (AnalysisTab.IsEnabled)
            WorkspaceTabs.SelectedItem = AnalysisTab;
    }

    private async void WorkspaceTabs_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not TabControl tabs || AnalysisTab is null) return;
        if (tabs.SelectedItem == AnalysisTab)
            await RefreshOutputPreviewAsync();
        else
            StopOutputVideoPreviewPlayback();
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
            OutputVideoWidth,
            OutputVideoHeight,
            OutputVideoFrameRate,
            maxReplaySpeed ? 10 : replaySpeed,
            ClockModeComboBox.SelectedIndex == 1 ? ReplayExportClock.Frame : ReplayExportClock.Recorded,
            PaintSurface.Mode,
            SourceFileName: Path.GetFileName(session.SourcePath),
            SourceSha256: session.SourceSha256,
            DecodeConfiguration: decodeConfiguration);
    }

    private ReplayScene CreateReplayScene(int logicalIndex)
    {
        if (replaySession is null) throw new InvalidOperationException("A decoded replay is required.");
        return ReplaySceneFactory.Create(
            replaySession.Seek(logicalIndex),
            replaySession.Count,
            replayExtent,
            reviewSession?.Diagnostics,
            markers,
            trailHistory?.Build(logicalIndex, trailMode, trailLength, trailVisibilityStart) ?? [],
            reverseX,
            reverseY);
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
            cancellationToken: cancellationToken);
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
            AnalysisSummaryText.Text = "Output preview unavailable";
            OutputHotspotText.Text = exception.Message;
            AnalysisHeatmapPreview.Show(null);
            ClearOutputVideoPreview();
        }
    }

    private void ApplyOutputPreview(CaptureAnalysisReport report)
    {
        if (session is null || decodeConfiguration is null) return;
        var range = report.Manifest.Range;
        var clockIsFrame = ClockModeComboBox.SelectedIndex == 1;
        var clockName = clockIsFrame ? "TP Frame 120 Hz" : "Recorded";
        var peak = report.Hotspot.Counts.Count == 0 ? 0 : report.Hotspot.Counts.Max();
        var profile = string.IsNullOrWhiteSpace(decodeConfiguration.Desay97Profile)
            ? string.Empty
            : $" / {decodeConfiguration.Desay97Profile}";
        var speed = maxReplaySpeed ? "MAX" : $"{replaySpeed:0.##}×";

        AnalysisSummaryText.Text = $"Previewing frames {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}";
        OutputRangeText.Text = $"{report.Events.Count:N0} frames\n{range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}";
        OutputFindingsText.Text = $"{report.DiagnosticAggregates.Count:N0} groups\n{report.Asil.UnresolvedAlarmGroups:N0} alarms";
        OutputHotspotText.Text = $"{report.Hotspot.SampleCount:N0} samples, peak {peak:N0}";
        OutputConfigurationText.Text =
            $"decoder  {decodeConfiguration.EventBufferVersion}{profile}\n" +
            $"source   {session.Probe.DisplayName}\n" +
            $"evidence {report.Manifest.FormatEvidenceStatus}\n" +
            $"paint    {PaintSurface.Mode}\n" +
            $"replay   {clockName} at {speed}";
        OutputSourceText.Text = $"{Path.GetFileName(session.SourcePath)}\nSHA-256\n{session.SourceSha256}";
        AnalysisHeatmapPreview.Show(report.Hotspot);
        PrepareOutputVideoPreview(range, clockName, speed);
    }

    private void PrepareOutputVideoPreview(AnalysisRange range, string clockName, string speed)
    {
        if (replaySession is null) return;
        var options = CreateReplayExportOptions("preview.mp4", range);
        outputVideoPlan = ReplayFramePlan.Build(replaySession, options);
        outputVideoFrameCount = ReplayFramePlan.OutputFrameCount(outputVideoPlan);
        outputVideoFrameIndex = 0;
        var duration = TimeSpan.FromSeconds(outputVideoFrameCount / (double)OutputVideoFrameRate);
        OutputClockText.Text = $"{FormatClock(duration)}\n{outputVideoFrameCount:N0} frames at {OutputVideoFrameRate} FPS";
        OutputVideoBadgeText.Text = $"{OutputVideoWidth} × {OutputVideoHeight}  /  {OutputVideoFrameRate} FPS  /  {speed}";
        OutputVideoTimeline.SetFrameCount(outputVideoFrameCount, OutputVideoFrameRate);
        OutputPreviewPlayPauseButton.IsEnabled = outputVideoFrameCount > 0;
        ShowOutputVideoFrame(0);
        OutputConfigurationText.Text += $"\nvideo    {clockName} / {OutputVideoFrameRate} FPS / {speed}";
    }

    private void ShowOutputVideoFrame(int outputFrameIndex)
    {
        if (replaySession is null || outputVideoFrameCount == 0 || outputVideoPlan.Count == 0) return;
        outputVideoFrameIndex = Math.Clamp(outputFrameIndex, 0, outputVideoFrameCount - 1);
        var logicalIndex = ReplayFramePlan.LogicalIndexAt(outputVideoPlan, outputVideoFrameIndex);
        OutputVideoPreview.Show(CreateReplayScene(logicalIndex), PaintSurface.Mode);
        OutputVideoTimeline.SetPosition(outputVideoFrameIndex);
        var currentTime = TimeSpan.FromSeconds(outputVideoFrameIndex / (double)OutputVideoFrameRate);
        var duration = TimeSpan.FromSeconds(outputVideoFrameCount / (double)OutputVideoFrameRate);
        OutputPreviewClockText.Text = $"{FormatClock(currentTime)} / {FormatClock(duration)}";
        OutputPreviewFrameText.Text = $"output {outputVideoFrameIndex + 1:N0}/{outputVideoFrameCount:N0}  source {logicalIndex + 1:N0}/{replaySession.Count:N0}";
    }

    private void OutputVideoTimeline_OnSeekRequested(object? sender, OutputVideoSeekEventArgs e)
    {
        StopOutputVideoPreviewPlayback();
        ShowOutputVideoFrame(e.OutputFrameIndex);
    }

    private async void OutputPreviewPlayPauseButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (outputVideoFrameCount == 0) return;
        if (outputVideoPlaybackCancellation is not null)
        {
            StopOutputVideoPreviewPlayback();
            return;
        }
        if (outputVideoFrameIndex >= outputVideoFrameCount - 1)
            ShowOutputVideoFrame(0);

        var cancellation = new CancellationTokenSource();
        outputVideoPlaybackCancellation = cancellation;
        SetOutputVideoPlaybackVisualState(true);
        try
        {
            while (outputVideoFrameIndex < outputVideoFrameCount - 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(1d / OutputVideoFrameRate), cancellation.Token);
                ShowOutputVideoFrame(outputVideoFrameIndex + 1);
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
        OutputPreviewPlayPauseButton.Content = playing ? "Ⅱ  Pause" : "▶  Preview";
        AutomationProperties.SetName(OutputPreviewPlayPauseButton, playing ? "Pause MP4 preview" : "Play MP4 preview");
    }

    private void ClearOutputVideoPreview()
    {
        StopOutputVideoPreviewPlayback();
        outputVideoPlan = [];
        outputVideoFrameCount = 0;
        outputVideoFrameIndex = 0;
        OutputVideoPreview.Clear();
        OutputVideoTimeline.SetFrameCount(0, OutputVideoFrameRate);
        OutputPreviewPlayPauseButton.IsEnabled = false;
        OutputPreviewClockText.Text = "00:00.000 / 00:00.000";
        OutputPreviewFrameText.Text = "output 0/0";
    }

    private void Desay97ProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (session is not null && Desay97ProfileComboBox.SelectedIndex >= 0)
            ConfigurationHintText.Text = "Palm profile selected · close the menu to confirm and decode.";
    }

    private async void EventVersionComboBox_OnDropDownClosed(object? sender, EventArgs e)
    {
        var isDesay97 = EventVersionComboBox.SelectedItem is ComboBoxItem versionItem &&
                        versionItem.Content?.ToString() == "0x97";
        if (session is not null && !isDesay97 && EventVersionComboBox.SelectedIndex >= 0)
            await DecodeSelectedAsync();
    }

    private async void Desay97ProfileComboBox_OnDropDownClosed(object? sender, EventArgs e)
    {
        var isDesay97 = EventVersionComboBox.SelectedItem is ComboBoxItem versionItem &&
                        versionItem.Content?.ToString() == "0x97";
        if (session is not null && isDesay97 && Desay97ProfileComboBox.SelectedIndex >= 0)
            await DecodeSelectedAsync();
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
            (EventVersionComboBox.SelectedIndex != 4 || Desay97ProfileComboBox.SelectedIndex >= 0);
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
            InspectorLogicalText.Text = "—";
            InspectorPhysicalText.Text = "—";
            InspectorTouchesText.Text = "—";
            InspectorCrcText.Text = "—";
            InspectorAsilText.Text = "—";
            InspectorAllBreakText.Text = "—";
            DecodedFieldsText.Text = "No physical source record matches the current Raw Explorer query.";
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
        MarkerQaCaseTextBox.Text = selectedMarkerId is null
            ? string.Empty
            : string.Join(", ", markers.FirstOrDefault(marker => marker.Id == selectedMarkerId)?.QaCaseIds ?? []);
        ReviewActionsPanel.IsVisible = true;
        ReviewOccurrenceComboBox.ItemsSource = row.Group.Occurrences
            .Select((occurrence, index) => new ReviewOccurrenceRow(index + 1, occurrence))
            .ToArray();
        ReviewOccurrenceComboBox.SelectedIndex = 0;
        UpdateReviewState(row.Group);
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

    private void PauseOnAlarmCheckBox_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (reviewSession is not null)
        {
            var enabled = PauseOnAlarmCheckBox.IsChecked == true;
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
            $"lifecycle={group.AsilLifecycle?.ToString() ?? "—"} · disposition={group.Disposition}" +
            (string.IsNullOrWhiteSpace(qa) ? string.Empty : $"\nQA={qa}") +
            (string.IsNullOrWhiteSpace(evidence) || evidence == "—" ? string.Empty : $"\nevidence={evidence}");
    }

    private void CreateReviewSession(IReadOnlyList<ReplayDiagnostic> diagnostics)
    {
        var previousState = reviewSession?.ExportState() ?? [];
        baseDiagnostics = diagnostics;
        var logicalIndices = decodedRows
            .SelectMany(row => row.PhysicalRecords.Append(row.Source).Select(source => (source.StableId, row.LogicalIndex)))
            .GroupBy(item => item.StableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().LogicalIndex, StringComparer.Ordinal);
        reviewSession = new ReviewSession(
            diagnostics.Concat(markers.Select(MarkerDiagnostic)).ToArray(),
            logicalIndices,
            new ReviewSessionOptions(PauseOnAlarmCheckBox.IsChecked == true, PauseOnAlarmCheckBox.IsChecked == true));
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
            marker.IsRange ? $"{marker.Label} · frames {marker.StartLogicalIndex + 1}–{marker.EndLogicalIndex + 1}" : $"{marker.Label} · frame {marker.StartLogicalIndex + 1}",
            source?.StableId ?? session?.SourceSha256 ?? "sidecar",
            source?.Location ?? new SourceLocation(0, 0),
            new Dictionary<string, string>
            {
                ["marker_id"] = marker.Id,
                ["qa_case_ids"] = string.Join(", ", marker.QaCaseIds ?? []),
                ["evidence"] = evidence.Count == 0 ? "—" : string.Join("; ", evidence.Select(item => $"{item.Kind}:{item.Label ?? Path.GetFileName(item.Path)}:{(File.Exists(item.Path) ? "available" : "missing")}")),
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
        var selected = selectGroupId is null ? null : reviewRows.FirstOrDefault(row => row.Group.Id == selectGroupId);
        if (selected is not null) DiagnosticListBox.SelectedItem = selected;
    }

    private void AddMarkerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0) return;
        var start = loopIn ?? currentLogicalIndex;
        var end = loopOut ?? currentLogicalIndex;
        if (end < start) (start, end) = (end, start);
        var marker = new ReplayMarker(
            $"marker-{Guid.NewGuid():N}",
            $"Marker {markers.Count + 1}",
            start,
            end,
            DateTimeOffset.UtcNow);
        markers.Add(marker);
        CreateReviewSession(baseDiagnostics);
        RefreshReviewQueue($"ANNOTATION_MARKER:{marker.Id}");
        SessionStatusText.Text = marker.IsRange
            ? $"Added range marker · frames {start + 1}–{end + 1}"
            : $"Added marker · frame {start + 1}";
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
                    ["pauseOnAlarm"] = PauseOnAlarmCheckBox.IsChecked == true,
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
        if (loaded.Document.VisibilityPreferences.GetValueOrDefault("pauseOnAlarm")) PauseOnAlarmCheckBox.IsChecked = true;
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

    private void ShowRecord(SourceRecord record, object? frame)
    {
        var registerReadable = record.SourceFields?.GetValueOrDefault("register_readable");
        InspectorTitleText.Text = frame is null && registerReadable is not null ? "Register Activity" : "Frame Summary";
        InspectorSubtitleText.Text = frame switch
        {
            CommonEventBufferFrame common =>
                $"Physical #{record.Index} · Common {VersionText(common.Version)} · {(common.HostStateEligible ? "Host-state" : "Evidence only")}",
            Desay97Frame desay =>
                $"Physical #{record.Index} · Desay 0x97 / {ProfileText(desay.Profile)} · {(desay.HostStateEligible ? "Host-state" : "Evidence only")}",
            _ when registerReadable is not null =>
                $"Physical #{record.Index} · {registerReadable} · {record.SourceFields?.GetValueOrDefault("register_profile") ?? "profile unknown"}",
            _ => $"Physical #{record.Index} · Raw source record",
        };
        InspectorLogicalText.Text = currentLogicalIndex >= 0 && replaySession is not null
            ? $"{currentLogicalIndex + 1} / {replaySession.Count}"
            : "—";
        InspectorPhysicalText.Text = record.Index.ToString(CultureInfo.InvariantCulture);
        InspectorTouchesText.Text = frame switch
        {
            CommonEventBufferFrame common => common.NumTouches.ToString(CultureInfo.InvariantCulture),
            Desay97Frame desay => desay.NumTouches.ToString(CultureInfo.InvariantCulture),
            _ => "—",
        };
        InspectorCrcText.Text = frame switch
        {
            CommonEventBufferFrame common => common.CrcValid ? "OK" : "FAIL",
            Desay97Frame desay => desay.CrcValid ? "OK" : "FAIL",
            _ => "—",
        };
        InspectorAsilText.Text = frame switch
        {
            CommonEventBufferFrame common => $"0x{common.Asil.Raw:X2}",
            Desay97Frame desay => desay.TpAsilError ? "TP alarm" : "—",
            _ => "—",
        };
        InspectorAllBreakText.Text = frame switch
        {
            CommonEventBufferFrame common => common.AllBreak.ToString(),
            Desay97Frame desay => desay.AllBreak.ToString(),
            _ => "—",
        };

        var frameAlerts = reviewSession?.Diagnostics
            .Where(item => item.Severity >= DiagnosticSeverity.Warning &&
                           (item.SourceRecordId == record.StableId || item.Location.LineNumber == record.Location.LineNumber))
            .Take(2)
            .ToArray() ?? [];
        var crcFailed = frame switch
        {
            CommonEventBufferFrame common => !common.CrcValid,
            Desay97Frame desay => !desay.CrcValid,
            _ => false,
        };
        var asilAlarm = frame switch
        {
            CommonEventBufferFrame common => common.TpAsilError || common.Asil.Alarm,
            Desay97Frame desay => desay.TpAsilError,
            _ => false,
        };
        InspectorAlertBorder.IsVisible = frameAlerts.Length > 0 || crcFailed || asilAlarm;
        InspectorAlertText.Text = frameAlerts.Length > 0
            ? string.Join(" · ", frameAlerts.Select(item => item.Code))
            : crcFailed ? "CRC validation failed" : asilAlarm ? "ASIL alarm active" : string.Empty;
        SourceLineText.Text = record.Location.LineNumber.ToString(CultureInfo.InvariantCulture);
        SourceOffsetText.Text = record.Location.ByteOffset.ToString(CultureInfo.InvariantCulture);
        StableIdText.Text = record.StableId;
        TransportText.Text = FormatTransport(record);
        var rawLayout = RawFrameLayoutBuilder.Build(record, frame);
        RawLayoutTitleText.Text = rawLayout.Title;
        RawLayoutSummaryText.Text = rawLayout.Summary;
        RawByteSectionsItemsControl.ItemsSource = rawLayout.Sections;
        RawBytesText.Text = FormatBytes(record.Data);
        DecodedFieldsText.Text = frame switch
        {
            CommonEventBufferFrame common => FormatDecodedFields(common),
            Desay97Frame desay => FormatDecodedFields(desay),
            _ when registerReadable is not null =>
                $"{registerReadable}\nraw={record.SourceFields?.GetValueOrDefault("register_value") ?? FormatBytes(record.Data)}\n" +
                $"profile-resolution={record.SourceFields?.GetValueOrDefault("register_profile_resolution") ?? "unknown"}",
            _ => "No decoded event is linked to this physical record.",
        };
    }

    private async void SourceAdapterComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringSourceChoice || pendingSourcePath is not { } path ||
            SourceAdapterComboBox.SelectedItem is not SourceAdapterChoice choice)
            return;
        await OpenCaptureAsync(path, choice.AdapterId);
    }

    private static string FormatTransport(SourceRecord record)
    {
        var text = new StringBuilder()
            .Append(record.Operation).Append(' ').Append(record.Target).Append(' ')
            .Append(record.Address is { } address ? $"0x{address:X}" : "address=—")
            .AppendLine()
            .Append("declared=").Append(record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "—")
            .Append(" actual=").Append(record.Data.Count);
        if (record.I2c is { } i2c)
        {
            text.AppendLine().Append($"slave=0x{i2c.SlaveAddress:X2} address-ack={AckText(i2c.AddressAcknowledged)}");
            text.AppendLine().Append("write commands=")
                .Append(i2c.WriteCommands.Count == 0 ? "—" : string.Join(" | ", i2c.WriteCommands.Select(FormatBytes)));
            text.AppendLine().Append("data ACKs=")
                .Append(i2c.Acked.Count == 0 ? "—" : string.Join(' ', i2c.Acked.Select(value => value ? "ACK" : "NAK")));
            if (!string.IsNullOrWhiteSpace(i2c.Error)) text.AppendLine().Append("transport error=").Append(i2c.Error);
        }
        if (record.SourceFields is { Count: > 0 })
        {
            text.AppendLine().AppendLine().Append("source fields");
            foreach (var field in record.SourceFields.OrderBy(item => item.Key, StringComparer.Ordinal))
                text.AppendLine().Append(field.Key).Append('=').Append(field.Value);
        }
        return text.ToString();
    }

    private static string AckText(bool? value) => value switch { true => "ACK", false => "NAK", null => "—" };

    private sealed record SourceAdapterChoice(string AdapterId, string DisplayName, ProbeConfidence Confidence)
    {
        public override string ToString() => $"{DisplayName} · {Confidence}";
    }

    private void ClearSession()
    {
        StopPlayback();
        session = null;
        replaySession = null;
        trailHistory = null;
        trailVisibilityStart = 0;
        allRawRows = [];
        registerActivities = [];
        rawRowsById = [];
        decodedRowsBySourceId = [];
        decodedRows = [];
        diagnosticRows = [];
        reviewSession = null;
        reviewRows = [];
        reviewFilter = ReviewQueueFilter.All;
        selectedReviewGroupId = null;
        selectedMarkerId = null;
        baseDiagnostics = [];
        markers.Clear();
        decodeConfiguration = null;
        pendingSidecar = null;
        currentLogicalIndex = -1;
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
        AnnotationMetadataPanel.IsVisible = false;
        MarkerQaCaseTextBox.Text = string.Empty;
        EventVersionComboBox.SelectedIndex = -1;
        EventVersionComboBox.IsEnabled = false;
        Desay97ProfileComboBox.SelectedIndex = -1;
        Desay97ProfileComboBox.IsVisible = false;
        DecodeButton.IsEnabled = false;
        PaintTab.IsEnabled = false;
        PreviousFrameButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        NextFrameButton.IsEnabled = false;
        LoopToggleButton.IsEnabled = false;
        LoopToggleButton.IsChecked = false;
        ClearLoopButton.IsEnabled = false;
        AddMarkerButton.IsEnabled = false;
        SaveReviewButton.IsEnabled = false;
        LoadReviewButton.IsEnabled = false;
        ExportAnalysisButton.IsEnabled = false;
        ExportReplayOutputButton.IsEnabled = false;
        ExportOutputPackageButton.IsEnabled = false;
        AnalysisTab.IsEnabled = false;
        outputPreviewCancellation?.Cancel();
        outputPreviewCancellation?.Dispose();
        outputPreviewCancellation = null;
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
        LoadReviewButton.IsVisible = true;
        ApplySidecarButton.IsVisible = false;
        ReplayTimelineSurface.IsEnabled = false;
        ReplayTimelineSurface.SetMaximum(1);
        ReplayTimelineSurface.SetPosition(0);
        ReplayTimelineSurface.SetSupportingProgress(0, 0);
        ReplayTimelineSurface.SetLoopRange(null, null, false);
        ReplayClockText.Text = "00:00.000";
        ReplayEndClockText.Text = "00:00.000";
        LoopRangeText.Text = "Loop —";
        PaintSurface.Fit();
        PaintZoomText.Text = "100%";
        PaintZoomHintBorder.IsVisible = false;
        PaintModeComboBox.SelectedIndex = 0;
        TrailModeComboBox.SelectedIndex = 1;
        TrailLengthComboBox.SelectedIndex = 2;
        TrailLengthComboBox.IsVisible = false;
        TrailLengthLabel.IsVisible = false;
        GridStrengthToggleButton.IsChecked = false;
        ReverseXToggleButton.IsChecked = false;
        ReverseYToggleButton.IsChecked = false;
        LegendPositionComboBox.SelectedIndex = 0;
        LegendVisibleToggleButton.IsChecked = true;
        LegendCompactToggleButton.IsChecked = true;
        reverseX = false;
        reverseY = false;
        legendPosition = ReplayLegendPosition.Auto;
        PaintSurface.SetStrongGrid(false);
        PaintSurface.SetLegendPosition(ReplayLegendPosition.TopLeft);
        PaintSurface.SetLegendVisible(true);
        PaintSurface.SetLegendCollapsed(true);
        PaintSurface.Clear();
        DiagnosticCountText.Text = "0";
        InspectorTitleText.Text = "Frame Summary";
        InspectorSubtitleText.Text = "Select a physical record or decoded event.";
        InspectorLogicalText.Text = "—";
        InspectorPhysicalText.Text = "—";
        InspectorTouchesText.Text = "—";
        InspectorCrcText.Text = "—";
        InspectorAsilText.Text = "—";
        InspectorAllBreakText.Text = "—";
        InspectorAlertBorder.IsVisible = false;
        InspectorAlertText.Text = string.Empty;
        SourceAdapterText.Text = "Probing…";
        SourceConfidenceText.Text = "Source and Event Buffer format remain separate";
        SourceHashText.Text = "SHA-256 appears after loading";
        TimelineSummaryText.Text = "0 physical · 0 logical · 0 evidence";
    }

    private void SetBusy(bool busy, string status)
    {
        operationInProgress = busy;
        LoadingProgress.IsVisible = busy;
        LoadButton.IsVisible = !busy;
        CancelButton.IsVisible = busy;
        DecodeButton.IsEnabled = !busy && session is not null;
        EventVersionComboBox.IsEnabled = !busy && session is not null;
        Desay97ProfileComboBox.IsEnabled = !busy && session is not null;
        RegisterProfileComboBox.IsEnabled = !busy && session is not null;
        ExportReadableLogButton.IsEnabled = !busy && session is not null;
        SaveReviewButton.IsEnabled = !busy && decodeConfiguration is not null;
        LoadReviewButton.IsEnabled = !busy && decodeConfiguration is not null;
        ExportAnalysisButton.IsEnabled = !busy && replaySession is { Count: > 0 };
        ExportReplayOutputButton.IsEnabled = !busy && replaySession is { Count: > 0 };
        ExportOutputPackageButton.IsEnabled = !busy && replaySession is { Count: > 0 };
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
        PreviousFrameButton.IsEnabled = replaySession.Count > 0;
        PlayPauseButton.IsEnabled = replaySession.Count > 0;
        NextFrameButton.IsEnabled = replaySession.Count > 0;
        LoopToggleButton.IsEnabled = replaySession.Count > 1;
        ReplayTimelineSurface.IsEnabled = replaySession.Count > 0;
        ReplayTimelineSurface.SetMaximum(maximum);
        replayExtent = ReplayExtent.Measure(replaySession.AllReportedContacts);
        trailHistory = ReplayTrailHistory.Create(replaySession);
        trailVisibilityStart = 0;
        RefreshLegendPlacement();
        PanelWidthTextBox.Text = replayExtent.MaximumX.ToString("0", CultureInfo.InvariantCulture);
        PanelHeightTextBox.Text = replayExtent.MaximumY.ToString("0", CultureInfo.InvariantCulture);
        PaintSurface.Fit();
        UpdatePaintZoomText();
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
    }

    private void SeekReplay(int logicalIndex, bool crossfade = false)
    {
        if (replaySession is null || replaySession.Count == 0)
        {
            return;
        }

        var clampedIndex = Math.Clamp(logicalIndex, 0, replaySession.Count - 1);
        var snapshot = replaySession.Seek(clampedIndex);
        currentLogicalIndex = clampedIndex;
        PaintSurface.Show(ReplaySceneFactory.Create(
            snapshot,
            replaySession.Count,
            replayExtent,
            reviewSession?.Diagnostics,
            markers,
            trailHistory?.Build(clampedIndex, trailMode, trailLength, trailVisibilityStart) ?? [],
            reverseX,
            reverseY), crossfade);
        ReplayClockText.Text = FormatClock(SelectedTime(snapshot.Timeline));
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
        ReplayTimelineSurface.SetPosition(clampedIndex);
        var physicalMaximum = Math.Max(1, session?.Records.Count - 1 ?? 1);
        var physicalValue = Math.Min(physicalMaximum, snapshot.PrimarySource.Index);
        var evidenceMaximum = Math.Max(1, diagnosticRows.Length);
        var evidenceValue = diagnosticRows.Count(row =>
            row.Diagnostic.Location.LineNumber <= snapshot.PrimarySource.Location.LineNumber);
        ReplayTimelineSurface.SetSupportingProgress(
            physicalValue / (double)physicalMaximum,
            evidenceValue / (double)evidenceMaximum);

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

        ShowRecord(snapshot.PhysicalRecords[^1], snapshot.DecodedFrame);
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
        if (currentLogicalIndex >= replaySession.Count - 1)
        {
            SeekReplay(loopEnabled ? loopIn ?? 0 : 0);
        }
        playbackCancellation = new CancellationTokenSource();
        SetPlaybackVisualState(playing: true);
        var nextIndex = Math.Min(currentLogicalIndex + 1, replaySession.Count - 1);
        TimelineStatusText.Text =
            $"Playing {(maxReplaySpeed ? "MAX" : $"{replaySpeed:0.##}×")} · " +
            $"next {DelayBetween(currentLogicalIndex, nextIndex).TotalMilliseconds:0.###} ms · " +
            $"frame interval {replaySession.FrameInterval.TotalMilliseconds:0.###} ms";
        _ = RunPlaybackAsync(playbackCancellation.Token);
    }

    private async Task RunPlaybackAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (replaySession is { Count: > 0 })
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = loopEnabled ? loopOut ?? replaySession.Count - 1 : replaySession.Count - 1;
                if (currentLogicalIndex >= end)
                {
                    if (loopEnabled && loopIn is { } start && start <= end)
                    {
                        SeekReplay(start, crossfade: true);
                        continue;
                    }
                    StopPlayback("Replay complete · use Home, seek, or Play to restart");
                    return;
                }

                var next = maxReplaySpeed
                    ? Math.Min(end, currentLogicalIndex + 50)
                    : currentLogicalIndex + 1;
                var pauseIndex = reviewSession?.FirstPauseIndex(currentLogicalIndex, next);
                if (pauseIndex is { } alarmIndex) next = alarmIndex;
                var delay = maxReplaySpeed
                    ? TimeSpan.FromMilliseconds(16)
                    : DelayBetween(currentLogicalIndex, next);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                SeekReplay(next);
                if (pauseIndex == next)
                {
                    SelectReviewAt(next);
                    StopPlayback("Paused on Alarm / QA Fail · acknowledge or continue when ready");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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

    private void StopPlayback(string? status = null)
    {
        var cancellation = playbackCancellation;
        playbackCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
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
        maxReplaySpeed = tag.Equals("max", StringComparison.OrdinalIgnoreCase);
        if (!maxReplaySpeed && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
        {
            replaySpeed = speed;
        }
        RefreshOutputPreviewIfVisible();
    }

    private void ClockModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (replaySession is not null && currentLogicalIndex >= 0)
        {
            ReplayClockText.Text = FormatClock(SelectedTime(replaySession.Timeline[currentLogicalIndex]));
            ReplayEndClockText.Text = FormatClock(SelectedEndTime());
        }
        RefreshOutputPreviewIfVisible();
    }

    private void PaintModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not SelectOption option ||
            !Enum.TryParse<ReplayRenderMode>(option.Value, out var mode))
            return;
        PaintSurface?.SetMode(mode);
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

        replayExtent = new ReplayExtent(width, height);
        PaintSurface.Fit();
        RefreshLegendPlacement();
        UpdatePaintZoomText();
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
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

        trailMode = selectedMode;
        if (TrailLengthComboBox is not null)
            TrailLengthComboBox.IsVisible = selectedMode == ReplayTrailMode.Recent;
        if (TrailLengthLabel is not null)
            TrailLengthLabel.IsVisible = false;
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
    }

    private void TrailLengthComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedLength))
            return;

        trailLength = selectedLength;
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
    }

    private void ClearTrailsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        trailVisibilityStart = Math.Max(0, currentLogicalIndex + 1);
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
        SessionStatusText.Text = "Visible trajectory history cleared · source data unchanged";
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

        legendPosition = selectedPosition;
        RefreshLegendPlacement();
    }

    private void LegendVisibleToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e) =>
        PaintSurface.SetLegendVisible(LegendVisibleToggleButton.IsChecked == true);

    private void LegendCompactToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e) =>
        PaintSurface.SetLegendCollapsed(LegendCompactToggleButton.IsChecked == true);

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
                : ReplayLegendPositioner.Choose(replaySession.AllReportedContacts, replayExtent, reverseX, reverseY);
        }
        PaintSurface.SetLegendPosition(resolved);
    }

    private void ReverseAxisToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        reverseX = ReverseXToggleButton.IsChecked == true;
        reverseY = ReverseYToggleButton.IsChecked == true;
        RefreshLegendPlacement();
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
    }

    private void GridStrengthToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        var strong = GridStrengthToggleButton.IsChecked == true;
        GridStrengthToggleButton.Content = strong ? "Grid +" : "Grid";
        PaintSurface.SetStrongGrid(strong);
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
        UpdateLoopText();
    }

    private void ClearLoopButton_OnClick(object? sender, RoutedEventArgs e)
    {
        loopIn = null;
        loopOut = null;
        loopEnabled = false;
        synchronizingLoopControls = true;
        try
        {
            LoopToggleButton.IsChecked = false;
        }
        finally
        {
            synchronizingLoopControls = false;
        }
        UpdateLoopText();
        TimelineStatusText.Text = "Loop range cleared · replay position unchanged";
    }

    private void ReplayTimelineSurface_OnLoopRangeChanged(object? sender, ReplayTimelineLoopEventArgs e)
    {
        loopIn = e.StartLogicalIndex;
        loopOut = e.EndLogicalIndex;
        loopEnabled = true;
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
    }

    private void UpdateLoopText()
    {
        var hasRange = loopIn is not null && loopOut is not null;
        LoopRangeText.Text = !hasRange
            ? "Loop —"
            : $"Loop {loopIn!.Value + 1:N0}–{loopOut!.Value + 1:N0}{(loopEnabled ? string.Empty : " · off")}";
        ClearLoopButton.IsEnabled = hasRange;
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control))
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
            case ReplayShortcutAction.TogglePlayback when PlayPauseButton.IsEnabled:
                PlayPauseButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.PreviousFrame when PreviousFrameButton.IsEnabled:
                PreviousFrameButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.NextFrame when NextFrameButton.IsEnabled:
                NextFrameButton_OnClick(this, new RoutedEventArgs());
                return true;
            case ReplayShortcutAction.JumpBackTenFrames when replaySession is { Count: > 0 }:
                JumpFrames(-10);
                return true;
            case ReplayShortcutAction.JumpForwardTenFrames when replaySession is { Count: > 0 }:
                JumpFrames(10);
                return true;
            case ReplayShortcutAction.FirstFrame when replaySession is { Count: > 0 }:
                StopPlayback();
                SeekReplay(0);
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

    private static bool IsShortcutEditingContext(object? source)
    {
        if (source is not Visual visual) return false;
        return visual is TextBox or ComboBox or ComboBoxItem or Slider or Button ||
               visual.FindAncestorOfType<TextBox>() is not null ||
               visual.FindAncestorOfType<ComboBox>() is not null ||
               visual.FindAncestorOfType<ComboBoxItem>() is not null ||
               visual.FindAncestorOfType<Slider>() is not null ||
               visual.FindAncestorOfType<Button>() is not null;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        outputPreviewCancellation?.Cancel();
        outputPreviewCancellation?.Dispose();
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
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
