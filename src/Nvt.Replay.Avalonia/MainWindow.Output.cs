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
    private int heatmapMinimumCount = 5;
    private double heatmapLabelThresholdRatio = 0.65;
    private string appliedHeatmapMode = "all";
    private bool outputFullscreenActive;
    private WindowState outputFullscreenPreviousWindowState = WindowState.Normal;
    private bool configuringOutputSettings;
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
    private int outputVideoFrameCount => outputVideoPreviewPlan?.Estimate.OutputFrameCount ?? 0;
    private int outputVideoFrameRate => outputVideoPreviewPlan?.Identity.Settings.FrameRate ?? outputWorkspace.Settings.FrameRate;
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

    private void RefreshOutputPreviewIfVisible()
    {
        if (WorkspaceTabs?.SelectedItem == AnalysisTab)
            _ = RefreshOutputPreviewAsync();
    }

    private sealed record OutputOperationIdentity(
        OutputReportIdentity ReportIdentity,
        long LoadGeneration,
        long DecodeGeneration);

}
