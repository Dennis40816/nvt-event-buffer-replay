using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Controls;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private CancellationTokenSource? operationCancellation;
    private CaptureSession? session;
    private ITouchReplaySession? replaySession;
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
    private int currentLogicalIndex = -1;
    private int? loopIn;
    private int? loopOut;
    private double replaySpeed = 1;
    private int trailLength = 10;
    private bool reverseX;
    private bool reverseY;
    private bool maxReplaySpeed;
    private bool synchronizingSelection;
    private string? pendingSourcePath;
    private bool configuringSourceChoice;
    private bool operationInProgress;
    private int themeMode;
    private bool reviewRailCollapsed;
    private bool inspectorRailCollapsed;
    private bool? reviewRailUserPreference;
    private bool? inspectorRailUserPreference;
    private const double ExpandedReviewRailWidth = 238;
    private const double ExpandedInspectorRailWidth = 312;
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
        Opened += (_, _) =>
        {
            ScheduleThemeContrast();
            ApplyResponsiveRails(Bounds.Width);
        };
        SizeChanged += MainWindow_OnSizeChanged;
        if (Application.Current is { } application) application.ActualThemeVariantChanged += Application_OnActualThemeVariantChanged;
    }

    private void MainWindow_OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsiveRails(e.NewSize.Width);

    private void ApplyResponsiveRails(double width)
    {
        SetReviewRailCollapsed(reviewRailUserPreference ?? width < 1280);
        SetInspectorRailCollapsed(inspectorRailUserPreference ?? width < 1180);
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
        ReviewRailToggleButton.Content = collapsed ? "›" : "‹";
        ToolTip.SetTip(ReviewRailToggleButton, collapsed ? "Open review queue" : "Collapse review queue");
    }

    private void SetInspectorRailCollapsed(bool collapsed)
    {
        inspectorRailCollapsed = collapsed;
        WorkspaceShellGrid.ColumnDefinitions[2].Width = new GridLength(collapsed ? 42 : ExpandedInspectorRailWidth);
        InspectorRailTitle.IsVisible = !collapsed;
        InspectorRailContent.IsVisible = !collapsed;
        InspectorRailToggleButton.Content = collapsed ? "‹" : "›";
        ToolTip.SetTip(InspectorRailToggleButton, collapsed ? "Open inspector" : "Collapse inspector");
    }

    private void Application_OnActualThemeVariantChanged(object? sender, EventArgs e) => ScheduleThemeContrast();

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

    private void CommandPaletteAction_OnClick(object? sender, RoutedEventArgs e)
    {
        var action = (sender as Button)?.Tag?.ToString();
        CloseCommandPalette();
        switch (action)
        {
            case "load": LoadButton_OnClick(sender, e); break;
            case "decode" when DecodeButton.IsEnabled: DecodeButton_OnClick(sender, e); break;
            case "play" when PlayPauseButton.IsEnabled: PlayPauseButton_OnClick(sender, e); break;
            case "step" when NextFrameButton.IsEnabled: NextFrameButton_OnClick(sender, e); break;
            case "mark" when AddMarkerButton.IsEnabled: AddMarkerButton_OnClick(sender, e); break;
            case "analysis" when ExportAnalysisButton.IsEnabled: ExportAnalysisButton_OnClick(sender, e); break;
            case "video" when AnalysisTab.IsEnabled: ExportReplayButton_OnClick(sender, e); break;
            case "theme": ThemeButton_OnClick(sender, e); break;
        }
    }

    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        themeMode = (themeMode + 1) % 2;
        var variant = themeMode == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current is { } application) application.RequestedThemeVariant = variant;
        ThemeButton.Content = themeMode == 0 ? "Dark" : "Light";
        SessionStatusText.Text = $"Color theme · {ThemeButton.Content?.ToString()?.Replace("Theme: ", string.Empty)}";
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
            var rawRows = await Task.Run(
                () => session.Records.Select(record => new RawRecordRow(record)).ToArray(),
                cancellationToken);

            rawRowsById = rawRows.ToDictionary(row => row.Record.StableId, StringComparer.Ordinal);
            RawRecordsList.ItemsSource = rawRows;
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
            ConfigurationHintText.Text = "Confirm the version to decode automatically; source detection never infers it.";
            TimelineSummaryText.Text = $"{session.Records.Count:N0} physical · 0 logical · {diagnosticRows.Length:N0} evidence";
            TimelineStatusText.Text = "Raw capture indexed · confirm Event Buffer Version to open Paint";
            WorkspaceTabs.SelectedIndex = 0;
            if (rawRows.Length > 0)
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

    internal async Task ApplyStartupDecodeAsync(string eventVersion, string? palmProfile)
    {
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
                session.Probe.AdapterId);
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
            TimelineStatusText.Text = "Logical replay ready · Space play/pause · ←/→ step · I/O loop";
            InitializeReplay();
            SaveReviewButton.IsEnabled = true;
            LoadReviewButton.IsEnabled = true;
            ExportAnalysisButton.IsEnabled = true;
            AnalysisTab.IsEnabled = true;
            AnalysisSummaryText.Text = $"Ready · {decodedRows.Length:N0} frames · export the full replay or current In/Out range";
            AddMarkerButton.IsEnabled = decodedRows.Length > 0;
            WorkspaceTabs.SelectedIndex = 2;
            if (decodedRows.Length > 0)
            {
                SeekReplay(0);
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
            var range = loopIn is { } start && loopOut is { } end
                ? new AnalysisRange(Math.Min(start, end), Math.Max(start, end))
                : new AnalysisRange(0, replaySession.Count - 1);
            var evidence = decodeConfiguration.EventBufferVersion is "0x83" or "0x84"
                ? EvidenceStatus.Verified
                : EvidenceStatus.Provisional;
            var report = await Task.Run(() => new CaptureAnalyzer().Analyze(
                session.SourcePath,
                session.SourceSha256,
                decodeConfiguration,
                evidence,
                replaySession,
                reviewSession.Diagnostics,
                reviewSession,
                range,
                cancellationToken: cancellationToken), cancellationToken);
            var result = await new AnalysisOutputWriter().WriteAsync(directory, report, cancellationToken);
            AnalysisSummaryText.Text =
                $"{report.Events.Count:N0} frames · {report.DiagnosticAggregates.Count:N0} finding groups · " +
                $"{report.Asil.Assertions} ASIL assert / {report.Asil.Clears} clear · {report.Hotspot.SampleCount:N0} hotspot samples";
            AnalysisOutputText.Text =
                $"{result.Directory}\nanalysis-report.json · events.json/csv · diagnostics.json/csv · manifest.json · heatmap.png\n" +
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
            var range = loopIn is { } start && loopOut is { } end
                ? new AnalysisRange(Math.Min(start, end), Math.Max(start, end))
                : new AnalysisRange(0, replaySession.Count - 1);
            var options = new ReplayExportOptions(
                path,
                range,
                1280,
                720,
                30,
                maxReplaySpeed ? 10 : replaySpeed,
                ClockModeComboBox.SelectedIndex == 1 ? ReplayExportClock.Frame : ReplayExportClock.Recorded,
                PaintSurface.Mode,
                SourceFileName: Path.GetFileName(session.SourcePath),
                SourceSha256: session.SourceSha256,
                DecodeConfiguration: decodeConfiguration);
            var result = await new ReplayRangeExporter().ExportAsync(
                replaySession,
                index => ReplaySceneFactory.Create(
                    replaySession.Seek(index),
                    replaySession.Count,
                    replayExtent,
                    reviewSession?.Diagnostics,
                    markers,
                    ReplaySceneFactory.BuildTrails(replaySession, index, trailLength),
                    reverseX,
                    reverseY),
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

    private void RawRecordsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RawRecordsList.SelectedItem is RawRecordRow row)
        {
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
        reviewRows = reviewSession?.Filter(reviewFilter).Select(group => new ReviewGroupRow(group)).ToArray() ?? [];
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
        InspectorTitleText.Text = $"Physical #{record.Index}";
        InspectorSubtitleText.Text = frame switch
        {
            CommonEventBufferFrame common =>
                $"Common {VersionText(common.Version)} · {(common.HostStateEligible ? "Host-State eligible" : "Evidence only")}",
            Desay97Frame desay =>
                $"Desay 0x97 / {ProfileText(desay.Profile)} · 2 physical transactions · {(desay.HostStateEligible ? "Host-State eligible" : "Evidence only")}",
            _ => "Raw physical record · semantic decoding not available for this transaction",
        };
        SourceLineText.Text = record.Location.LineNumber.ToString(CultureInfo.InvariantCulture);
        SourceOffsetText.Text = record.Location.ByteOffset.ToString(CultureInfo.InvariantCulture);
        StableIdText.Text = record.StableId;
        TransportText.Text = FormatTransport(record);
        RawBytesText.Text = FormatBytes(record.Data);
        DecodedFieldsText.Text = frame switch
        {
            CommonEventBufferFrame common => FormatDecodedFields(common),
            Desay97Frame desay => FormatDecodedFields(desay),
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
        RawRecordsList.ItemsSource = null;
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
        LoopInButton.IsEnabled = false;
        LoopOutButton.IsEnabled = false;
        AddMarkerButton.IsEnabled = false;
        SaveReviewButton.IsEnabled = false;
        LoadReviewButton.IsEnabled = false;
        ExportAnalysisButton.IsEnabled = false;
        AnalysisTab.IsEnabled = false;
        AnalysisSummaryText.Text = "Decode a capture to generate analysis.";
        AnalysisOutputText.Text = "No output written";
        LoadReviewButton.IsVisible = true;
        ApplySidecarButton.IsVisible = false;
        ReplaySeekSlider.Maximum = 1;
        ReplaySeekSlider.Value = 0;
        PhysicalTimelineProgress.Maximum = 1;
        PhysicalTimelineProgress.Value = 0;
        LogicalTimelineProgress.Maximum = 1;
        LogicalTimelineProgress.Value = 0;
        EvidenceTimelineProgress.Maximum = 1;
        EvidenceTimelineProgress.Value = 0;
        ReplayClockText.Text = "00:00.000";
        ReplayEndClockText.Text = "00:00.000";
        LoopRangeText.Text = "Loop —";
        PaintStatusText.Text = "Decode a capture to replay";
        PaintSurface.Fit();
        PaintZoomText.Text = "100%";
        ReverseXToggleButton.IsChecked = false;
        ReverseYToggleButton.IsChecked = false;
        reverseX = false;
        reverseY = false;
        PaintSurface.Clear();
        DiagnosticCountText.Text = "0";
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
        SaveReviewButton.IsEnabled = !busy && decodeConfiguration is not null;
        LoadReviewButton.IsEnabled = !busy && decodeConfiguration is not null;
        ExportAnalysisButton.IsEnabled = !busy && replaySession is { Count: > 0 };
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
        LoopInButton.IsEnabled = replaySession.Count > 0;
        LoopOutButton.IsEnabled = replaySession.Count > 0;
        ReplaySeekSlider.Maximum = maximum;
        LogicalTimelineProgress.Maximum = maximum;
        PhysicalTimelineProgress.Maximum = Math.Max(1, session.Records.Count - 1);
        EvidenceTimelineProgress.Maximum = Math.Max(1, diagnosticRows.Length);
        replayExtent = ReplayExtent.Measure(replaySession.AllReportedContacts);
        PanelWidthTextBox.Text = replayExtent.MaximumX.ToString("0", CultureInfo.InvariantCulture);
        PanelHeightTextBox.Text = replayExtent.MaximumY.ToString("0", CultureInfo.InvariantCulture);
        PaintSurface.Fit();
        UpdatePaintZoomText();
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
    }

    private void SeekReplay(int logicalIndex)
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
            ReplaySceneFactory.BuildTrails(replaySession, clampedIndex, trailLength),
            reverseX,
            reverseY));
        var activeIds = snapshot.HostContacts.Where(contact => contact.IsActive).Select(contact => $"#{contact.Id}").ToArray();
        PaintStatusText.Text =
            $"FRAME {clampedIndex + 1:00}/{replaySession.Count:00}  ·  ACTIVE {(activeIds.Length == 0 ? "—" : string.Join(' ', activeIds))}" +
            (snapshot.ReportedContacts.Any(contact => contact.Status == TouchStatus.Break) ? "  ·  BREAK" : string.Empty) +
            (snapshot.GlobalPalm ? "  ·  GLOBAL PALM" : string.Empty) +
            (!snapshot.HostStateUpdated ? "  ·  EVIDENCE ONLY" : string.Empty);
        ReplayClockText.Text = FormatClock(SelectedTime(snapshot.Timeline));
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
        LogicalTimelineProgress.Value = clampedIndex;
        PhysicalTimelineProgress.Value = Math.Min(PhysicalTimelineProgress.Maximum, snapshot.PrimarySource.Index);
        EvidenceTimelineProgress.Value = diagnosticRows.Count(row =>
            row.Diagnostic.Location.LineNumber <= snapshot.PrimarySource.Location.LineNumber);

        synchronizingSelection = true;
        try
        {
            ReplaySeekSlider.Value = clampedIndex;
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
            SeekReplay(loopIn ?? 0);
        }
        playbackCancellation = new CancellationTokenSource();
        PlayPauseButton.Content = "Ⅱ PAUSE";
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
                var end = loopOut ?? replaySession.Count - 1;
                if (currentLogicalIndex >= end)
                {
                    if (loopIn is { } start && start <= end)
                    {
                        SeekReplay(start);
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
            PlayPauseButton.Content = "▶ PLAY";
        }
        if (TimelineStatusText is not null && replaySession is { Count: > 0 } &&
            (status is not null || cancellation is not null))
        {
            TimelineStatusText.Text = status ?? "Paused · Space play/pause · ←/→ step · I/O loop";
        }
    }

    private void ReplaySeekSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (!synchronizingSelection && replaySession is not null)
        {
            StopPlayback();
            SeekReplay((int)Math.Round(e.NewValue));
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
    }

    private void ClockModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (replaySession is not null && currentLogicalIndex >= 0)
        {
            ReplayClockText.Text = FormatClock(SelectedTime(replaySession.Timeline[currentLogicalIndex]));
            ReplayEndClockText.Text = FormatClock(SelectedEndTime());
        }
    }

    private void PaintModeComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var mode = (sender as ComboBox)?.SelectedIndex switch
        {
            0 => ReplayRenderMode.HostState,
            1 => ReplayRenderMode.ReportedFrame,
            _ => ReplayRenderMode.Compare,
        };
        PaintSurface?.SetMode(mode);
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
        UpdatePaintZoomText();
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
        SessionStatusText.Text = $"Panel coordinate space · {width:0} × {height:0} · fit to canvas";
    }

    private void PaintZoomOutButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PaintSurface.ZoomOut();
        UpdatePaintZoomText();
    }

    private void PaintZoomInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PaintSurface.ZoomIn();
        UpdatePaintZoomText();
    }

    private void PaintFitButton_OnClick(object? sender, RoutedEventArgs e)
    {
        PaintSurface.Fit();
        UpdatePaintZoomText();
    }

    private void UpdatePaintZoomText() =>
        PaintZoomText.Text = PaintSurface.ZoomFactor.ToString("P0", CultureInfo.InvariantCulture);

    private void TrailLengthComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var selectedLength))
            return;

        trailLength = selectedLength;
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
    }

    private void ReverseAxisToggleButton_OnIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        reverseX = ReverseXToggleButton.IsChecked == true;
        reverseY = ReverseYToggleButton.IsChecked == true;
        if (replaySession is not null && currentLogicalIndex >= 0)
            SeekReplay(currentLogicalIndex);
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
        UpdateLoopText();
    }

    private void UpdateLoopText()
    {
        LoopRangeText.Text = loopIn is null && loopOut is null
            ? "Loop —"
            : $"Loop {(loopIn + 1)?.ToString(CultureInfo.InvariantCulture) ?? "—"}–{(loopOut + 1)?.ToString(CultureInfo.InvariantCulture) ?? "—"}";
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
        else if (e.Key == Key.O && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            LoadButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control) && SaveReviewButton.IsEnabled)
        {
            SaveReviewButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.E && e.KeyModifiers.HasFlag(KeyModifiers.Control) && ExportAnalysisButton.IsEnabled)
        {
            ExportAnalysisButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            PlayPauseButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            PreviousFrameButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Right)
        {
            NextFrameButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Home && replaySession is not null)
        {
            StopPlayback();
            SeekReplay(0);
            e.Handled = true;
        }
        else if (e.Key == Key.End && replaySession is not null)
        {
            StopPlayback();
            SeekReplay(replaySession.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.I)
        {
            LoopInButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.O)
        {
            LoopOutButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.M)
        {
            AddMarkerButton_OnClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
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
