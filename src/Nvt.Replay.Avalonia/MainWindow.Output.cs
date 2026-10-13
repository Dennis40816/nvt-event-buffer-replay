using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Controls;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private readonly CancellationSlot _outputPreviewCancellation = new();
    private readonly CancellationSlot _outputVideoPlaybackCancellation = new();
    private readonly ReplayOutputWorkspace outputWorkspace = new();
    private readonly ExportJobService outputExportJobs = new();
    private readonly OutputWindowState _outputState = new();
    private readonly OutputPreviewState _outputPreview = new();
    private CaptureAnalysisReport? currentOutputReport;
    private readonly EventSuppressionScope configuringOutputSettings = new();
    private readonly EventSuppressionScope configuringOutputRange = new();
    internal ReplayOutputSettings OutputSettings => outputWorkspace.Settings;
    internal long OutputSettingsRevision => outputWorkspace.Revision;
    internal ReplayOutputPlanIdentity? OutputPreviewPlanIdentity => _outputPreview.Plan?.Identity;
    internal ReplayFramePlanSnapshot? OutputPreviewFramePlan => _outputPreview.Plan?.FramePlan;
    internal int OutputPreviewPlanBuildCount => _outputState.PlanService.BuildCount;
    internal int OutputReportBuildCount => _outputState.ReportService.BuildCount;
    internal int OutputVideoRenderCount => OutputVideoPreview.RgbRenderCount;
    internal bool OutputVideoPreviewDirty => _outputPreview.Dirty;
    internal CaptureAnalysisReport? CurrentOutputReport => currentOutputReport;
    internal Action<string>? OutputFolderRevealActionForTesting { get => TestSeams.OutputFolderRevealAction; set => TestSeams.OutputFolderRevealAction = value; }
    internal Func<CancellationToken, Task<FfmpegProbeResult>>? FfmpegProbeForTesting { get => TestSeams.FfmpegProbe; set => TestSeams.FfmpegProbe = value; }
    internal OutputReportInputs CaptureCurrentOutputReportInputsForTesting(AnalysisRange range) =>
        CaptureOutputReportInputs(range);
    internal string OutputReplayIdentityForTesting => OutputReplayIdentity();
    internal void ReplaceOutputPlanServiceForTesting(ReplayOutputPlanService replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        ResetOutputPlanning();
        _outputState.PlanService.Dispose();
        _outputState.PlanService = replacement;
        ClearOutputVideoPreview();
    }
    internal void ReplaceOutputReportServiceForTesting(OutputReportService replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        _outputState.ReportService.Reset();
        _outputState.ReportService.Dispose();
        _outputState.ReportService = replacement;
        currentOutputReport = null;
    }
    private int outputVideoFrameCount => _outputPreview.Plan?.Estimate.OutputFrameCount ?? 0;
    private int outputVideoFrameRate => _outputPreview.Plan?.Identity.Settings.FrameRate ?? outputWorkspace.Settings.FrameRate;
    private void OutputContentComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutputContentComboBox?.SelectedItem is not SelectOption option ||
            OutputVideoPanel is null || OutputHeatmapPanel is null ||
            OutputPointPlotPanel is null || OutputPackagePanel is null)
            return;

        var mp4 = option.Value == "mp4";
        var heatmap = option.Value == "heatmap";
        var points = option.Value == "points";
        var outputTypeChanged = outputWorkspace.Update(outputWorkspace.Settings with
        {
            ContentType = option.Value switch
            {
                "heatmap" => ReplayOutputContentType.Heatmap,
                "points" => ReplayOutputContentType.ReportedPointPlot,
                "package" => ReplayOutputContentType.DataPackage,
                _ => ReplayOutputContentType.Mp4Video,
            },
        });
        OutputVideoPanel.IsVisible = mp4;
        OutputHeatmapPanel.IsVisible = heatmap;
        OutputPointPlotPanel.IsVisible = points;
        OutputPackagePanel.IsVisible = option.Value == "package";
        OutputModeTitleText.Text = option.Label.ToUpperInvariant();
        ExportSelectedOutputButton.Content = option.Value switch
        {
            "heatmap" => "Export PNG",
            "points" => "Export PNG",
            "package" => "Export package",
            _ => "Export MP4",
        };
        OutputContentDescriptionText.Text = option.Description;
        AutomationProperties.SetName(ExportSelectedOutputButton, ExportSelectedOutputButton.Content?.ToString());
        RefreshOutputInfo();
        if (!mp4) StopOutputVideoPreviewPlayback();
        if (!mp4) OutputExportWarningPanel.IsVisible = false;
        if (heatmap) RefreshHeatmapPresentation();
        if (points) RefreshPointPlotPresentation();
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
        if (minimumCount == _outputState.Heatmap.MinimumCount &&
            Math.Abs(nextLabelRatio - _outputState.Heatmap.LabelThresholdRatio) < 0.0001 &&
            string.Equals(selectedMode, _outputState.Heatmap.Mode, StringComparison.Ordinal))
            return;
        _outputState.Heatmap = new HeatmapSettings(minimumCount, nextLabelRatio, selectedMode);
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
        else if (value == "points")
            ExportPointPlotButton_OnClick(sender, e);
        else
            ExportReplayButton_OnClick(sender, e);
    }

    private void ExportPointPlotButton_OnClick(object? sender, RoutedEventArgs e) => FailureReporter.Runner.Run("output.export-points", _ => ExportPointPlotAsync(sender, e), CancellationToken.None);
    private async Task ExportPointPlotAsync(object? sender, RoutedEventArgs e)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || replaySession.Count == 0) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export reported point history",
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.SourcePath) + ".reported-points.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;

        var lease = _operation.Begin();
        var cancellationToken = lease.Token;
        try
        {
            var operation = CaptureOutputOperation(SelectedOutputRange());
            SetBusy(true, "Rendering reported point PNG");
            var reportSnapshot = await GetOutputReportAsync(operation.ReportIdentity, cancellationToken);
            ThrowIfStaleOutputOperation(operation);
            currentOutputReport = reportSnapshot.Report;
            RefreshPointPlotPresentation();
            await WriteCurrentPointPlotPreviewAsync(path, currentOutputReport, cancellationToken);
            ThrowIfStaleOutputOperation(operation);
            SetActualOutputSize(path);
            AnalysisOutputText.Text =
                $"Reported point PNG · {path}\n{AnalysisPointPlotPreview.PointCount:N0} points · " +
                $"range {currentOutputReport.Manifest.Range.StartLogicalIndex + 1:N0}-{currentOutputReport.Manifest.Range.EndLogicalIndex + 1:N0}";
            SessionStatusText.Text = "Reported point PNG exported";
        }
        catch (OperationCanceledException)
        {
            if (_operation.IsCurrent(lease))
                SessionStatusText.Text = "Point plot export cancelled; no partial output was kept";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (_operation.IsCurrent(lease))
                SessionStatusText.Text = $"Point plot export failed · {exception.Message}";
        }
        finally
        {
            if (_operation.Release(lease)) SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private async Task WriteCurrentPointPlotPreviewAsync(
        string path,
        CaptureAnalysisReport report,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalysisPointPlotPreview.ClearHover();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        var width = outputWorkspace.Settings.Width;
        var height = outputWorkspace.Settings.Height;
        var exportSurface = new AnalysisPointPlotSurface
        {
            Width = width,
            Height = height,
            SurfaceBrush = AnalysisPointPlotPreview.SurfaceBrush,
            GridBrush = AnalysisPointPlotPreview.GridBrush,
            TextBrush = AnalysisPointPlotPreview.TextBrush,
            ToolTipSurfaceBrush = AnalysisPointPlotPreview.ToolTipSurfaceBrush,
            ToolTipBorderBrush = AnalysisPointPlotPreview.ToolTipBorderBrush,
            RenderThemeOverride = AnalysisPointPlotPreview.ActualThemeVariant == ThemeVariant.Light
                ? ReplayRenderTheme.Light
                : ReplayRenderTheme.Dark,
        };
        exportSurface.Show(report, width, height);
        exportSurface.Measure(new Size(width, height));
        exportSurface.Arrange(new Rect(0, 0, width, height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(exportSurface);
        await using var memory = new MemoryStream();
        bitmap.Save(memory, PngBitmapEncoderOptions.Default);
        var png = memory.ToArray();
        await Nvt.Core.IO.AtomicOutput.WriteAsync(
            path,
            (stream, token) => stream.WriteAsync(png, token).AsTask(),
            cancellationToken);
    }

    private void ExportHeatmapButton_OnClick(object? sender, RoutedEventArgs e) => FailureReporter.Runner.Run("output.export-heatmap", _ => ExportHeatmapAsync(sender, e), CancellationToken.None);
    private async Task ExportHeatmapAsync(object? sender, RoutedEventArgs e)
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

        var lease = _operation.Begin();
        var cancellationToken = lease.Token;
        try
        {
            var operation = CaptureOutputOperation(SelectedOutputRange());
            SetBusy(true, "Rendering heatmap PNG");
            var reportSnapshot = await GetOutputReportAsync(operation.ReportIdentity, cancellationToken);
            ThrowIfStaleOutputOperation(operation);
            var report = reportSnapshot.Report;
            currentOutputReport = report;
            RefreshHeatmapPresentation();
            await WriteCurrentHeatmapPreviewAsync(path, cancellationToken);
            ThrowIfStaleOutputOperation(operation);
            SetActualOutputSize(path);
            var mode = SelectedHeatmapMode() == "repeated" ? $"repeated N≥{_outputState.Heatmap.MinimumCount:N0}" : "all recorded points";
            AnalysisOutputText.Text = $"Heatmap PNG · {path}\n{AnalysisHeatmapPreview.VisibleSampleCount:N0} visible samples · {mode} · range {report.Manifest.Range.StartLogicalIndex + 1:N0}-{report.Manifest.Range.EndLogicalIndex + 1:N0}";
            SessionStatusText.Text = "Heatmap PNG exported";
        }
        catch (OperationCanceledException)
        {
            if (_operation.IsCurrent(lease))
                SessionStatusText.Text = "Heatmap export cancelled; no partial output was kept";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            if (_operation.IsCurrent(lease))
                SessionStatusText.Text = $"Heatmap export failed · {exception.Message}";
        }
        finally
        {
            if (_operation.Release(lease)) SetBusy(false, SessionStatusText.Text ?? "Ready");
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
        await Nvt.Core.IO.AtomicOutput.WriteAsync(
            path,
            (stream, token) => stream.WriteAsync(png, token).AsTask(),
            cancellationToken);
    }

    private void ExportAnalysisButton_OnClick(object? sender, RoutedEventArgs e) => FailureReporter.Runner.Run("output.export-analysis", _ => ExportAnalysisAsync(sender, e), CancellationToken.None);
    private async Task ExportAnalysisAsync(object? sender, RoutedEventArgs e)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewWorkspace is null || replaySession.Count == 0) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export analysis outputs",
            AllowMultiple = false,
        });
        var directory = folders.SingleOrDefault()?.TryGetLocalPath();
        if (directory is null) return;

        var lease = _operation.Begin();
        var cancellationToken = lease.Token;
        try
        {
            var operation = CaptureOutputOperation(SelectedOutputRange());
            SetBusy(true, "Analyzing selected replay range");
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
            SetActualOutputSize(result.Directory);
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
            if (_operation.IsCurrent(lease))
                SessionStatusText.Text = "Analysis cancelled; prior complete output was preserved";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_operation.IsCurrent(lease))
                SessionStatusText.Text = $"Analysis export failed · {exception.Message}";
        }
        finally
        {
            if (_operation.Release(lease)) SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private void ExportReplayButton_OnClick(object? sender, RoutedEventArgs e) => FailureReporter.Runner.Run("output.export-replay", _ => BeginReplayExportAsync(longExportConfirmed: false), CancellationToken.None);

    private void OutputExportContinueButton_OnClick(object? sender, RoutedEventArgs e) => FailureReporter.Runner.Run("output.export-continue", _ => BeginReplayExportAsync(longExportConfirmed: true), CancellationToken.None);

    private void OutputExportDismissButton_OnClick(object? sender, RoutedEventArgs e)
    {
        OutputExportWarningPanel.IsVisible = false;
        SessionStatusText.Text = _outputState.EncoderWarning
            ? "MP4 encoder warning dismissed; no output was created"
            : "Long MP4 export not started";
        _outputState.EncoderWarning = false;
    }

    private void OutputExportCancelButton_OnClick(object? sender, RoutedEventArgs e)
    {
        CancelOutputExportJob();
    }

    private async Task BeginReplayExportAsync(bool longExportConfirmed)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || replaySession.Count == 0 || outputExportJobs.IsActive) return;
        FfmpegProbeResult encoderProbe;
        try
        {
            encoderProbe = FfmpegProbeForTesting is { } probe
                ? await probe(_outputPreviewCancellation.Current?.Token ?? CancellationToken.None)
                : await FfmpegRuntime.ProbeAsync(cancellationToken: _outputPreviewCancellation.Current?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "FFmpeg probe cancelled; no output was created";
            return;
        }
        catch (ReplayEncoderUnavailableException exception)
        {
            ShowOutputEncoderWarning(exception.Message);
            return;
        }
        outputWorkspace.Update(outputWorkspace.Settings with
        {
            ContentType = ReplayOutputContentType.Mp4Video,
            Range = SelectedOutputRange(),
        });
        ReplayOutputPreviewPlan preview;
        try
        {
            _outputPreview.SetRequestPending(true);
            UpdateOutputExportButtonAvailability();
            preview = await _outputState.PlanService.GetPreviewAsync(
                replaySession,
                OutputReplayIdentity(),
                outputWorkspace.Settings,
                _outputPreviewCancellation.Current?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "MP4 planning was cancelled; no output was created";
            return;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            _outputPreview.FailRequest(exception.Message);
            AnalysisSummaryText.Text = "Output preview unavailable";
            OutputHotspotText.Text = exception.Message;
            SessionStatusText.Text = $"MP4 export unavailable · {exception.Message}";
            ClearOutputVideoPreview();
            return;
        }
        finally
        {
            _outputPreview.CompleteRequest();
            UpdateOutputExportButtonAvailability();
        }
        if (preview.Identity.Settings != outputWorkspace.Settings)
        {
            SessionStatusText.Text = "Output settings changed; review the refreshed MP4 preview before exporting.";
            RefreshOutputPreviewIfVisible();
            return;
        }
        if (!ReferenceEquals(_outputPreview.Plan?.FramePlan, preview.FramePlan) ||
            _outputPreview.Plan.Identity != preview.Identity)
            PrepareOutputVideoPreview(preview, PreviewClockName(preview.Identity.Settings), $"{preview.Identity.Settings.Speed:0.##}×");
        var estimate = preview.Estimate;
        var settings = preview.Identity.Settings;
        if (!longExportConfirmed && estimate.RequiresConfirmation)
        {
            _outputState.EncoderWarning = false;
            OutputExportWarningTitleText.Text = "Long MP4 export";
            OutputExportContinueButton.IsVisible = true;
            OutputExportDismissButton.Content = "Cancel";
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

        var options = CreateReplayExportOptions(path) with
        {
            FfmpegPath = encoderProbe.ExecutablePath,
            AllowPngSequenceFallback = false,
        };
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
            SetActualOutputSize(result.OutputPath);
            AnalysisOutputText.Text =
                $"{result.Kind} · {result.OutputPath}\n{result.OutputFrameCount:N0} frames · {result.Duration.TotalSeconds:0.###} s\nmanifest={result.ManifestPath}" +
                (result.Warning is null ? string.Empty : $"\nWARNING: {result.Warning}");
            SessionStatusText.Text = result.Kind == ReplayExportKind.Mp4
                ? "Replay MP4 exported"
                : "FFmpeg unavailable · PNG sequence exported safely";
            RevealOutputFolder(result.OutputPath);
        }
        else if (completed.Status == ExportJobStatus.Cancelled)
        {
            SessionStatusText.Text = "Replay export cancelled; no partial output was kept";
        }
        else if (completed.Status == ExportJobStatus.Failed)
        {
            SessionStatusText.Text = $"Replay export failed · {completed.Error?.Message ?? "Unknown export error"}";
            if (completed.Error is ReplayEncoderUnavailableException encoderException)
                ShowOutputEncoderWarning(encoderException.Message);
        }
    }

    private void ShowOutputEncoderWarning(string message)
    {
        _outputState.EncoderWarning = true;
        OutputExportWarningTitleText.Text = "MP4 encoder unavailable";
        OutputExportWarningText.Text = message;
        OutputExportContinueButton.IsVisible = false;
        OutputExportDismissButton.Content = "Close";
        OutputExportWarningPanel.IsVisible = true;
        SessionStatusText.Text = "MP4 export blocked · FFmpeg requires attention";
    }

    internal bool RevealOutputFolder(string outputPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Directory.Exists(fullPath)
                ? fullPath
                : Path.GetDirectoryName(fullPath);
            if (directory is null || !Directory.Exists(directory)) return false;
            if (OutputFolderRevealActionForTesting is { } reveal)
            {
                reveal(directory);
                return true;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return false;
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
        LoadButton.IsEnabled = !exporting && !_operation.Active;
        OutputExportActivityPanel.IsVisible = exporting;
        ExportSelectedOutputButton.IsVisible = !exporting;
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
        var previewReady = !_outputPreview.PlanPending && _outputPreview.PlanError is null &&
                           replaySession is not null &&
                           MatchingOutputVideoPreviewPlan(outputWorkspace.Settings, OutputReplayIdentityOrEmpty()) is not null;
        ExportSelectedOutputButton.IsEnabled =
            !_operation.Active && replaySession is { Count: > 0 } &&
            (!mp4Selected || !outputExportJobs.IsActive && previewReady);
    }

    private void OpenOutputPreviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (AnalysisTab.IsEnabled)
            WorkspaceTabs.SelectedItem = AnalysisTab;
    }

    private void OutputVideoSetting_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringOutputSettings.IsActive || OutputClockComboBox is null || OutputSpeedComboBox is null ||
            OutputFrameRateComboBox is null)
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

        SyncOutputResolutionWithPanel(updateWorkspace: false);
        ApplyOutputSettingsFromControls(showStatus: false);
    }

    private void OutputCustomSetting_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyOutputCustomSettings();
        e.Handled = true;
    }

    private void OutputCustomSetting_OnLostFocus(object? sender, RoutedEventArgs e) => ApplyOutputCustomSettings();

    private void OutputRangeTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyOutputRangeFromControls();
        e.Handled = true;
    }

    private void OutputRangeTextBox_OnLostFocus(object? sender, RoutedEventArgs e) =>
        ApplyOutputRangeFromControls();

    private void OutputRangeFullButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (replaySession is not { Count: > 0 }) return;
        _outputState.RangeUserDefined = false;
        ApplyOutputRange(new AnalysisRange(0, replaySession.Count - 1), showStatus: true);
    }

    private void ApplyOutputRangeFromControls()
    {
        if (configuringOutputRange.IsActive || replaySession is not { Count: > 0 }) return;
        if (!int.TryParse(OutputRangeStartTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) ||
            !int.TryParse(OutputRangeEndTextBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var end) ||
            start < 1 || end < start || end > replaySession.Count)
        {
            SessionStatusText.Text = $"Output range must use frames 1-{replaySession.Count:N0}, with From no greater than To.";
            SynchronizeOutputRangeControls(SelectedOutputRange());
            return;
        }

        _outputState.RangeUserDefined = true;
        ApplyOutputRange(new AnalysisRange(start - 1, end - 1), showStatus: true);
    }

    private void InitializeOutputRange()
    {
        if (replaySession is not { Count: > 0 })
        {
            _outputState.RangeUserDefined = false;
            ApplyOutputRange(new AnalysisRange(0, 0), showStatus: false, refreshPreview: false);
            return;
        }

        var existing = outputWorkspace.Settings.Range;
        var preserveExisting = _outputState.RangeUserDefined &&
                               existing.StartLogicalIndex >= 0 &&
                               existing.EndLogicalIndex >= existing.StartLogicalIndex &&
                               existing.EndLogicalIndex < replaySession.Count;
        var range = preserveExisting
            ? existing
            : new AnalysisRange(0, replaySession.Count - 1);
        if (!preserveExisting) _outputState.RangeUserDefined = false;
        ApplyOutputRange(range, showStatus: false, refreshPreview: false);
    }

    private void ApplyOutputRange(
        AnalysisRange range,
        bool showStatus,
        bool refreshPreview = true)
    {
        if (replaySession is { Count: > 0 })
        {
            var start = Math.Clamp(range.StartLogicalIndex, 0, replaySession.Count - 1);
            var end = Math.Clamp(range.EndLogicalIndex, start, replaySession.Count - 1);
            range = new AnalysisRange(start, end);
        }
        else
        {
            range = new AnalysisRange(0, 0);
        }

        var changed = outputWorkspace.Update(outputWorkspace.Settings with { Range = range });
        SynchronizeOutputRangeControls(range);
        OutputVideoTimeline.SetSourceRange(replaySession?.Count ?? 0, range.StartLogicalIndex, range.EndLogicalIndex);
        RefreshOutputInfo();
        if (!changed) return;
        if (showStatus)
            SessionStatusText.Text = $"Output range set to frames {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}";
        if (refreshPreview) RefreshOutputPreviewIfVisible();
    }

    private void SynchronizeOutputRangeControls(AnalysisRange range)
    {
        if (OutputRangeStartTextBox is null || OutputRangeEndTextBox is null) return;
        using (configuringOutputRange.Enter())
        {
            OutputRangeStartTextBox.Text = (range.StartLogicalIndex + 1).ToString(CultureInfo.InvariantCulture);
            OutputRangeEndTextBox.Text = (range.EndLogicalIndex + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

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
        RefreshOutputInfo();
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

        var width = NormalizeVideoDimension(CurrentPaintExtent.MaximumX, 320, ReplayOutputWorkspace.MaximumWidth);
        var height = NormalizeVideoDimension(CurrentPaintExtent.MaximumY, 180, ReplayOutputWorkspace.MaximumHeight);

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

    private bool SyncOutputResolutionWithPanel(bool updateWorkspace = true)
    {
        var width = NormalizeVideoDimension(CurrentPaintExtent.MaximumX, 320, ReplayOutputWorkspace.MaximumWidth);
        var height = NormalizeVideoDimension(CurrentPaintExtent.MaximumY, 180, ReplayOutputWorkspace.MaximumHeight);
        OutputInfoResolutionText.Text = $"Paint · {width} × {height}";
        var changed = updateWorkspace && outputWorkspace.Update(outputWorkspace.Settings with { Width = width, Height = height });
        RefreshOutputInfo();
        return changed;
    }

    private static int NormalizeVideoDimension(double value, int minimum, int maximum)
    {
        var result = (int)Math.Clamp(Math.Round(value), minimum, maximum);
        return result % 2 == 0 ? result : Math.Min(maximum, result + 1);
    }

    private string OutputVideoSettingsLabel() =>
        $"{outputWorkspace.Settings.Width} × {outputWorkspace.Settings.Height} / {outputWorkspace.Settings.FrameRate} FPS / {outputWorkspace.Settings.Speed:0.##}×";

    private void RefreshOutputInfo()
    {
        if (OutputInfoFormatText is null) return;
        var selectedType = (OutputContentComboBox.SelectedItem as SelectOption)?.Value ?? "mp4";
        var settings = outputWorkspace.Settings;
        var range = SelectedOutputRange();
        var rangeText = replaySession is { Count: > 0 }
            ? $"range {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}"
            : "range -";
        OutputInfoResolutionText.Text = $"Paint · {settings.Width} × {settings.Height}";

        switch (selectedType)
        {
            case "heatmap":
                {
                    var width = Math.Max(640, (int)Math.Ceiling(AnalysisHeatmapPreview.Bounds.Width));
                    var height = Math.Max(360, (int)Math.Ceiling(AnalysisHeatmapPreview.Bounds.Height));
                    var samples = currentOutputReport?.Hotspot.SampleCount ?? 0;
                    OutputInfoFormatText.Text = "PNG · heatmap";
                    OutputInfoResolutionText.Text = $"Preview · {width} × {height}";
                    OutputInfoSizeText.Text = FormatEstimatedRange(
                        EstimateRasterBytes(width, height, 0.12),
                        EstimateRasterBytes(width, height, 0.9));
                    OutputInfoDetailText.Text = $"{samples:N0} samples · {rangeText}";
                    break;
                }
            case "points":
                {
                    var samples = AnalysisPointPlotPreview?.PointCount ?? 0;
                    OutputInfoFormatText.Text = "PNG · reported points";
                    OutputInfoResolutionText.Text = $"Paint · {settings.Width} × {settings.Height}";
                    OutputInfoSizeText.Text = FormatEstimatedRange(
                        EstimateRasterBytes(settings.Width, settings.Height, 0.04),
                        EstimateRasterBytes(settings.Width, settings.Height, 0.55));
                    OutputInfoDetailText.Text = $"{samples:N0} points · {rangeText}";
                    break;
                }
            case "package":
                {
                    var events = currentOutputReport?.Events.Count ?? 0;
                    var records = session?.Records.Count ?? 0;
                    var diagnostics = currentOutputReport?.DiagnosticAggregates.Count ?? 0;
                    var centralEstimate = 24_000L + events * 720L + records * 132L + diagnostics * 1_600L;
                    OutputInfoFormatText.Text = "ZIP-ready · 9 files";
                    OutputInfoResolutionText.Text = $"Paint metadata · {settings.Width} × {settings.Height}";
                    OutputInfoSizeText.Text = FormatEstimatedRange(
                        Math.Max(8_192, (long)(centralEstimate * 0.7)),
                        Math.Max(16_384, (long)(centralEstimate * 1.35)));
                    OutputInfoDetailText.Text = $"{events:N0} events · {records:N0} records · {rangeText}";
                    break;
                }
            default:
                {
                    var frameCount = _outputPreview.Plan?.Estimate.OutputFrameCount ?? 0;
                    var duration = _outputPreview.Plan?.Estimate.Duration ?? TimeSpan.Zero;
                    OutputInfoFormatText.Text = "MP4 · H.264";
                    OutputInfoSizeText.Text = frameCount > 0
                        ? FormatEstimatedRange(
                            EstimateVideoBytes(settings.Width, settings.Height, frameCount, 0.01),
                            EstimateVideoBytes(settings.Width, settings.Height, frameCount, 0.05))
                        : _outputPreview.PlanPending ? "Calculating…" : "Waiting for preview";
                    OutputInfoDetailText.Text =
                        $"{settings.FrameRate} FPS · {frameCount:N0} frames · {FormatClock(duration)} · {rangeText}";
                    break;
                }
        }
        OutputInfoSizeLabelText.Text = "Estimated size";
    }

    private static long EstimateVideoBytes(int width, int height, long frameCount, double bitsPerPixelPerFrame) =>
        (long)Math.Ceiling(Math.Max(0, width) * (double)Math.Max(0, height) * Math.Max(0, frameCount) *
                          Math.Max(0, bitsPerPixelPerFrame) / 8d);

    private static long EstimateRasterBytes(int width, int height, double bytesPerPixel) =>
        (long)Math.Ceiling(Math.Max(0, width) * (double)Math.Max(0, height) * Math.Max(0, bytesPerPixel));

    private static string FormatEstimatedRange(long minimumBytes, long maximumBytes) =>
        $"Estimated {FormatFileSize(Math.Min(minimumBytes, maximumBytes))}-{FormatFileSize(Math.Max(minimumBytes, maximumBytes))}";

    private void SetActualOutputSize(string path)
    {
        try
        {
            var bytes = File.Exists(path)
                ? new FileInfo(path).Length
                : Directory.Exists(path)
                    ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
                    : -1;
            if (bytes < 0) return;
            OutputInfoSizeLabelText.Text = "Actual size";
            OutputInfoSizeText.Text = FormatFileSize(bytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The export succeeded. A size probe failure must not turn it into a failed export.
        }
    }

    private static string FormatFileSize(long bytes)
    {
        var size = Math.Max(0, bytes);
        if (size >= 1024L * 1024 * 1024)
            return $"{size / (1024d * 1024 * 1024):0.#} GB";
        if (size >= 1024L * 1024)
            return $"{size / (1024d * 1024):0.#} MB";
        if (size >= 1024L)
            return $"{size / 1024d:0.#} KB";
        return $"{size} B";
    }

    private AnalysisRange SelectedOutputRange()
    {
        if (replaySession is null || replaySession.Count == 0)
            return new AnalysisRange(0, 0);
        var range = outputWorkspace.Settings.Range;
        var start = Math.Clamp(range.StartLogicalIndex, 0, replaySession.Count - 1);
        var end = Math.Clamp(range.EndLogicalIndex, start, replaySession.Count - 1);
        return new AnalysisRange(start, end);
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
        _outputPreview.Plan is { } current &&
        ReferenceEquals(_outputPreview.Replay, replaySession) &&
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
        return _outputState.ReportService.GetAsync(
            identity,
            () => CaptureOutputReportInputs(identity.Range),
            cancellationToken);
    }

    private OutputOperationIdentity CaptureOutputOperation(AnalysisRange range) => new(
        CaptureOutputReportIdentity(range, OutputReplayIdentity()),
        _captureWorkspace.Pending);

    private bool IsCurrentOutputOperation(OutputOperationIdentity operation)
    {
        if (session is null || replaySession is null || decodeConfiguration is null || reviewWorkspace is null)
            return false;
        var current = CaptureOutputReportIdentity(operation.ReportIdentity.Range, OutputReplayIdentity());
        return operation.ReportIdentity.Matches(current) &&
               ReferenceEquals(operation.Pending, _captureWorkspace.Pending);
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
        var cancellationToken = _outputPreviewCancellation.Begin().Token;
        var generation = _outputPreview.BeginRequest();
        var range = SelectedOutputRange();
        outputWorkspace.Update(outputWorkspace.Settings with { Range = range });
        var settings = outputWorkspace.Settings;
        var replayIdentity = OutputReplayIdentity();
        AnalysisSummaryText.Text = $"Preparing frames {range.StartLogicalIndex + 1:N0}-{range.EndLogicalIndex + 1:N0}...";
        OutputHotspotText.Text = "Analyzing coordinates";
        _outputPreview.SetRequestPending(settings.ContentType == ReplayOutputContentType.Mp4Video);
        UpdateOutputExportButtonAvailability();
        try
        {
            ReplayOutputPreviewPlan? preview = null;
            if (settings.ContentType == ReplayOutputContentType.Mp4Video)
            {
                preview = await _outputState.PlanService.GetPreviewAsync(
                    replaySession,
                    replayIdentity,
                    settings,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != _outputPreview.Generation || settings != outputWorkspace.Settings) return;
            }
            var reportSnapshot = await GetOutputReportAsync(range, replayIdentity, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _outputPreview.Generation || settings != outputWorkspace.Settings) return;
            ApplyOutputPreview(reportSnapshot.Report, preview);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            if (generation != _outputPreview.Generation) return;
            currentOutputReport = null;
            _outputPreview.FailRequest(exception.Message);
            AnalysisSummaryText.Text = "Output preview unavailable";
            OutputHotspotText.Text = exception.Message;
            AnalysisHeatmapPreview.Show(null);
            AnalysisPointPlotPreview.Show(null, outputWorkspace.Settings.Width, outputWorkspace.Settings.Height);
            ClearOutputVideoPreview();
        }
        finally
        {
            if (generation == _outputPreview.Generation)
            {
                _outputPreview.CompleteRequest();
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
        RefreshPointPlotPresentation();
        if (preview is not null)
            PrepareOutputVideoPreview(preview, clockName, speed);
        RefreshOutputInfo();
    }

    private void RefreshHeatmapPresentation()
    {
        if (currentOutputReport is not { } report)
        {
            AnalysisHeatmapPreview.Show(null);
            return;
        }

        var repeated = SelectedHeatmapMode() == "repeated";
        var minimum = repeated ? _outputState.Heatmap.MinimumCount : 1;
        AnalysisHeatmapPreview.Show(report.Hotspot, minimum, _outputState.Heatmap.LabelThresholdRatio);
        var hiddenSamples = Math.Max(0, report.Hotspot.SampleCount - AnalysisHeatmapPreview.VisibleSampleCount);
        OutputHotspotText.Text = repeated
            ? $"N≥{minimum:N0} · {AnalysisHeatmapPreview.VisibleSampleCount:N0}/{report.Hotspot.SampleCount:N0} samples · peak {AnalysisHeatmapPreview.PeakCount:N0}"
            : $"{AnalysisHeatmapPreview.VisibleSampleCount:N0} samples · {AnalysisHeatmapPreview.VisibleCellCount:N0} cells · peak {AnalysisHeatmapPreview.PeakCount:N0}";
        HeatmapRangeText.Text =
            $"Frames {report.Manifest.Range.StartLogicalIndex + 1:N0}-{report.Manifest.Range.EndLogicalIndex + 1:N0} · " +
            $"{report.Hotspot.Columns} × {report.Hotspot.Rows} grid" +
            (hiddenSamples > 0 ? $" · {hiddenSamples:N0} samples below N" : string.Empty);
        RefreshOutputInfo();
    }

    private void RefreshPointPlotPresentation()
    {
        if (AnalysisPointPlotPreview is null) return;
        var settings = outputWorkspace.Settings;
        AnalysisPointPlotPreview.Show(currentOutputReport, settings.Width, settings.Height);
        if (currentOutputReport is not { } report)
        {
            OutputPointPlotText.Text = "Waiting for preview";
            PointPlotRangeText.Text = "Selected output range";
            return;
        }

        OutputPointPlotText.Text =
            $"{AnalysisPointPlotPreview.PointCount:N0} points · {AnalysisPointPlotPreview.ContactIdCount:N0} contact IDs";
        PointPlotRangeText.Text =
            $"Frames {report.Manifest.Range.StartLogicalIndex + 1:N0}-{report.Manifest.Range.EndLogicalIndex + 1:N0}";
        RefreshOutputInfo();
    }

    private void PrepareOutputVideoPreview(ReplayOutputPreviewPlan preview, string clockName, string speed)
    {
        if (replaySession is null) return;
        _outputPreview.InstallPlan(preview, replaySession);
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
        _outputPreview.MarkDirty();
        ShowOutputVideoFrame(_outputPreview.FrameIndex);
        OutputConfigurationText.Text += $"\nvideo    {clockName} / {settings.FrameRate} FPS / {speed}";
        RefreshOutputInfo();
    }

    private void ShowOutputVideoFrame(int outputFrameIndex)
    {
        if (replaySession is null || outputVideoFrameCount == 0 || _outputPreview.Plan is null ||
            !ReferenceEquals(_outputPreview.Replay, replaySession)) return;
        _outputPreview.FrameIndex = Math.Clamp(outputFrameIndex, 0, outputVideoFrameCount - 1);
        var logicalIndex = ReplayFramePlan.LogicalIndexAt(_outputPreview.Plan.Entries, _outputPreview.FrameIndex);
        var scene = CreateReplayScene(logicalIndex);
        var settings = CurrentReplayRenderSettings();
        var outputSettings = _outputPreview.Plan.Identity.Settings;
        OutputVideoPreview.Show(scene, settings, outputSettings.Width, outputSettings.Height);
        if (_outputState.FullscreenRestoreState.HasValue)
            OutputFullscreenVideoPreview.Show(scene, settings, outputSettings.Width, outputSettings.Height);
        _outputPreview.MarkRendered();
        OutputVideoTimeline.SetPosition(_outputPreview.FrameIndex);
        OutputFullscreenTimeline.SetPosition(_outputPreview.FrameIndex);
        var currentTime = TimeSpan.FromSeconds(_outputPreview.FrameIndex / (double)outputSettings.FrameRate);
        var duration = _outputPreview.Plan.Estimate.Duration;
        OutputPreviewClockText.Text = $"{FormatClock(currentTime)} / {FormatClock(duration)}";
        OutputPreviewFrameText.Text = $"output {_outputPreview.FrameIndex + 1:N0}/{outputVideoFrameCount:N0}  source {logicalIndex + 1:N0}/{replaySession.Count:N0}";
        OutputFullscreenClockText.Text = OutputPreviewClockText.Text;
        OutputFullscreenFrameText.Text = $"{_outputPreview.FrameIndex + 1:N0} / {outputVideoFrameCount:N0}";
    }

    private void OutputVideoTimeline_OnSeekRequested(object? sender, OutputVideoSeekEventArgs e)
    {
        StopOutputVideoPreviewPlayback();
        ShowOutputVideoFrame(e.OutputFrameIndex);
    }

    private void OutputVideoTimeline_OnRangeChanged(object? sender, OutputVideoRangeEventArgs e)
    {
        _outputState.RangeUserDefined = true;
        ApplyOutputRange(new AnalysisRange(e.StartLogicalIndex, e.EndLogicalIndex), showStatus: true);
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
        if (outputVideoFrameCount == 0 || _outputState.FullscreenRestoreState.HasValue) return;
        _outputState.FullscreenRestoreState = WindowState;
        OutputFullscreenOverlay.IsVisible = true;
        WindowState = WindowState.FullScreen;
        ShowOutputVideoFrame(_outputPreview.FrameIndex);
        OutputFullscreenOverlay.Focus();
    }

    private void ExitOutputFullscreen()
    {
        if (_outputState.FullscreenRestoreState is not { } previousWindowState) return;
        _outputState.FullscreenRestoreState = null;
        OutputFullscreenOverlay.IsVisible = false;
        OutputFullscreenVideoPreview.Clear();
        WindowState = previousWindowState;
    }

    private void OutputPreviewPlayPauseButton_OnClick(object? sender, RoutedEventArgs e) => FailureReporter.Runner.Run("output.preview-play-pause", _ => ToggleOutputPreviewPlaybackAsync(sender, e), CancellationToken.None);
    private async Task ToggleOutputPreviewPlaybackAsync(object? sender, RoutedEventArgs e)
    {
        if (outputVideoFrameCount == 0) return;
        if (_outputVideoPlaybackCancellation.IsActive)
        {
            StopOutputVideoPreviewPlayback();
            return;
        }
        var cancellation = _outputVideoPlaybackCancellation.Begin();
        SetOutputVideoPlaybackVisualState(true);
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (_outputPreview.FrameIndex >= outputVideoFrameCount - 1)
                    ShowOutputVideoFrame(0);

                var firstOutputFrame = _outputPreview.FrameIndex;
                var playbackStarted = FailureReporter.TimeProvider.GetTimestamp();
                while (_outputPreview.FrameIndex < outputVideoFrameCount - 1)
                {
                    var elapsed = FailureReporter.TimeProvider.GetElapsedTime(playbackStarted).TotalSeconds;
                    var targetFrame = Math.Min(
                        outputVideoFrameCount - 1,
                        firstOutputFrame + (int)Math.Floor(elapsed * outputVideoFrameRate));
                    if (targetFrame > _outputPreview.FrameIndex)
                        ShowOutputVideoFrame(targetFrame);
                    if (targetFrame >= outputVideoFrameCount - 1) break;

                    var nextFrameAt = (targetFrame - firstOutputFrame + 1d) / outputVideoFrameRate;
                    var delay = Math.Max(1, (int)Math.Round((nextFrameAt - elapsed) * 1000));
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), FailureReporter.TimeProvider, cancellation.Token);
                }

                if (OutputPreviewRepeatToggleButton.IsChecked != true) break;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, 1000 / outputVideoFrameRate)), FailureReporter.TimeProvider, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (_outputVideoPlaybackCancellation.ReleaseIfCurrent(cancellation))
            {
                SetOutputVideoPlaybackVisualState(false);
            }
        }
    }

    private void StopOutputVideoPreviewPlayback()
    {
        var cancellation = _outputVideoPlaybackCancellation.Take();
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
        _outputPreview.ClearPlan();
        OutputVideoBadgeText.Text = _outputPreview.PlanError ?? "Preview unavailable";
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
        RefreshOutputInfo();
    }

    private void InvalidateCurrentOutputVideoFrame()
    {
        _outputPreview.MarkDirty();
        RefreshCurrentOutputVideoFrame();
    }

    private void RefreshCurrentOutputVideoFrame()
    {
        var visible = _outputState.FullscreenRestoreState.HasValue || outputWorkspaceActive && OutputVideoPanel.IsVisible;
        if (!visible || !_outputPreview.Dirty || outputVideoFrameCount == 0 || _outputPreview.Plan is null)
            return;
        ShowOutputVideoFrame(_outputPreview.FrameIndex);
    }

    private void ResetOutputPlanning()
    {
        _outputPreview.AdvanceGeneration();
        _outputPreviewCancellation.CancelAndClear();
        _outputState.PlanService.Invalidate();
        _outputState.ReportService.Reset();
        _outputPreview.ResetRequest();
        currentOutputReport = null;
        AnalysisHeatmapPreview.Show(null);
        AnalysisPointPlotPreview.Show(null, outputWorkspace.Settings.Width, outputWorkspace.Settings.Height);
        ClearOutputVideoPreview();
    }

    private void RefreshOutputPreviewIfVisible()
    {
        if (WorkspaceTabs?.SelectedItem == AnalysisTab)
            _ = RefreshOutputPreviewAsync();
    }

    private sealed record OutputOperationIdentity(
        OutputReportIdentity ReportIdentity,
        PendingCaptureOperation? Pending);
}
