using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Controls;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private CancellationTokenSource? operationCancellation;
    private NdsCaptureSession? session;
    private CommonInspectionReport? report;
    private CommonReplaySession? replaySession;
    private Dictionary<string, RawRecordRow> rawRowsById = [];
    private DecodedFrameRow[] decodedRows = [];
    private DiagnosticRow[] diagnosticRows = [];
    private CancellationTokenSource? playbackCancellation;
    private int currentLogicalIndex = -1;
    private int? loopIn;
    private int? loopOut;
    private double replaySpeed = 1;
    private bool maxReplaySpeed;
    private bool synchronizingSelection;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void LoadButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load NDS communication log",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("NDS communication log") { Patterns = ["*.txt", "*.log"] },
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

    internal async Task OpenCaptureAsync(string path)
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
                () => NdsCaptureSession.LoadAsync(path, progress, cancellationToken),
                cancellationToken);
            var rawRows = await Task.Run(
                () => session.Records.Select(record => new RawRecordRow(record)).ToArray(),
                cancellationToken);

            rawRowsById = rawRows.ToDictionary(row => row.Record.StableId, StringComparer.Ordinal);
            RawRecordsList.ItemsSource = rawRows;
            CaptureNameText.Text = Path.GetFileName(session.SourcePath).ToUpperInvariant();
            SessionStatusText.Text = $"{session.Records.Count:N0} physical records indexed · semantic format required";
            SourceAdapterText.Text = session.Probe.DisplayName;
            SourceConfidenceText.Text = $"{session.Probe.Confidence} confidence · {session.Probe.Reasons.FirstOrDefault()}";
            SourceHashText.Text = $"SHA-256\n{session.SourceSha256}";
            EventVersionComboBox.IsEnabled = true;
            DecodeButton.IsEnabled = true;
            ConfigurationHintText.Text = "Select the operator-confirmed version; source detection does not infer it.";
            TimelineSummaryText.Text = $"{session.Records.Count:N0} physical · 0 logical · 0 evidence";
            TimelineStatusText.Text = "Raw capture indexed; semantic replay remains disabled until Decode";
            WorkspaceTabs.SelectedIndex = 0;
            if (rawRows.Length > 0)
            {
                RawRecordsList.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Load cancelled; no partial session was committed";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            SessionStatusText.Text = $"Load failed · {exception.Message}";
            ConfigurationHintText.Text = "Choose another file or verify that this is an NDS communication log.";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private async void DecodeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null)
        {
            return;
        }

        if (EventVersionComboBox.SelectedItem is not ComboBoxItem selected ||
            selected.Content is not string versionText ||
            !CommonEventBufferDecoder.TryParseVersion(versionText, out var version))
        {
            ConfigurationHintText.Text = "Event Buffer Version is required and must be confirmed by the operator.";
            return;
        }

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, $"Decoding Common {versionText}");

        try
        {
            report = await Task.Run(() => session.DecodeCommon(version), cancellationToken);
            replaySession = await Task.Run(() => new CommonReplaySession(report.Frames), cancellationToken);
            decodedRows = await Task.Run(
                () => report.Frames.Select((frame, index) => new DecodedFrameRow(index, frame)).ToArray(),
                cancellationToken);
            diagnosticRows = await Task.Run(
                () => report.Diagnostics
                    .Concat(replaySession.Diagnostics)
                    .Select(diagnostic => new DiagnosticRow(diagnostic))
                    .ToArray(),
                cancellationToken);

            DecodedFramesList.ItemsSource = decodedRows;
            DiagnosticListBox.ItemsSource = diagnosticRows;
            DiagnosticCountText.Text = diagnosticRows.Length.ToString(CultureInfo.InvariantCulture);
            SessionStatusText.Text = $"Common {versionText} · {decodedRows.Length:N0} decoded frames · {diagnosticRows.Length:N0} diagnostics";
            ConfigurationHintText.Text = $"Common {versionText} selected manually · raw source remains unchanged";
            TimelineSummaryText.Text = $"{session.Records.Count:N0} physical · {decodedRows.Length:N0} logical · {diagnosticRows.Length:N0} evidence";
            TimelineStatusText.Text = "Logical replay ready · Space play/pause · ←/→ step · I/O loop";
            InitializeReplay();
            WorkspaceTabs.SelectedIndex = 2;
            if (decodedRows.Length > 0)
            {
                SeekReplay(0);
            }
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Decode cancelled; previous complete result was preserved";
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

    private void RawRecordsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RawRecordsList.SelectedItem is RawRecordRow row)
        {
            ShowRecord(row.Record, FindFrame(row.Record.StableId));
            if (!synchronizingSelection && replaySession is not null)
            {
                var logicalIndex = Array.FindIndex(decodedRows, item => item.Frame.Source.StableId == row.Record.StableId);
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

        ShowRecord(row.Frame.Source, row.Frame);
        if (rawRowsById.TryGetValue(row.Frame.Source.StableId, out var rawRow))
        {
            RawRecordsList.SelectedItem = rawRow;
            RawRecordsList.ScrollIntoView(rawRow);
        }
    }

    private void DiagnosticListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DiagnosticListBox.SelectedItem is not DiagnosticRow row)
        {
            return;
        }

        if (rawRowsById.TryGetValue(row.Diagnostic.SourceRecordId, out var rawRow))
        {
            ShowRecord(rawRow.Record, FindFrame(rawRow.Record.StableId));
            RawRecordsList.SelectedItem = rawRow;
            RawRecordsList.ScrollIntoView(rawRow);
        }

        InspectorSubtitleText.Text = $"{row.Diagnostic.Severity} · {row.Diagnostic.Code} · {row.Diagnostic.Message}";
    }

    private CommonEventBufferFrame? FindFrame(string sourceId) =>
        report?.Frames.FirstOrDefault(frame => frame.Source.StableId == sourceId);

    private void ShowRecord(SourceRecord record, CommonEventBufferFrame? frame)
    {
        InspectorTitleText.Text = $"Physical #{record.Index}";
        InspectorSubtitleText.Text = frame is null
            ? "Raw physical record · semantic decoding not available for this transaction"
            : $"Common {VersionText(frame.Version)} · {(frame.HostStateEligible ? "Host-State eligible" : "Evidence only")}";
        SourceLineText.Text = record.Location.LineNumber.ToString(CultureInfo.InvariantCulture);
        SourceOffsetText.Text = record.Location.ByteOffset.ToString(CultureInfo.InvariantCulture);
        StableIdText.Text = record.StableId;
        TransportText.Text =
            $"{record.Operation} {record.Target} 0x{record.Address:X}\n" +
            $"declared={record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "—"} actual={record.Data.Count}";
        RawBytesText.Text = FormatBytes(record.Data);
        DecodedFieldsText.Text = frame is null
            ? "No Common event is linked to this physical record."
            : FormatDecodedFields(frame);
    }

    private void ClearSession()
    {
        StopPlayback();
        session = null;
        report = null;
        replaySession = null;
        rawRowsById = [];
        decodedRows = [];
        diagnosticRows = [];
        currentLogicalIndex = -1;
        loopIn = null;
        loopOut = null;
        RawRecordsList.ItemsSource = null;
        DecodedFramesList.ItemsSource = null;
        DiagnosticListBox.ItemsSource = null;
        EventVersionComboBox.SelectedIndex = -1;
        EventVersionComboBox.IsEnabled = false;
        DecodeButton.IsEnabled = false;
        PaintTab.IsEnabled = false;
        PreviousFrameButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        NextFrameButton.IsEnabled = false;
        LoopInButton.IsEnabled = false;
        LoopOutButton.IsEnabled = false;
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
        PaintStatusText.Text = "Decode a Common capture to replay";
        PaintSurface.Clear();
        DiagnosticCountText.Text = "0";
        SourceAdapterText.Text = "Probing…";
        SourceConfidenceText.Text = "Source and Event Buffer format remain separate";
        SourceHashText.Text = "SHA-256 appears after loading";
        TimelineSummaryText.Text = "0 physical · 0 logical · 0 evidence";
    }

    private void SetBusy(bool busy, string status)
    {
        LoadingProgress.IsVisible = busy;
        LoadButton.IsVisible = !busy;
        CancelButton.IsVisible = busy;
        DecodeButton.IsEnabled = !busy && session is not null;
        EventVersionComboBox.IsEnabled = !busy && session is not null;
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
        PaintSurface.SetExtent(report?.Frames ?? []);
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
        PaintSurface.Show(snapshot);
        PaintStatusText.Text =
            $"#{clampedIndex + 1:N0}/{replaySession.Count:N0} · reported {snapshot.ReportedContacts.Count} · host {snapshot.HostContacts.Count}" +
            (snapshot.GlobalPalm ? " · GLOBAL PALM" : string.Empty) +
            (!snapshot.HostStateUpdated ? " · EVIDENCE ONLY" : string.Empty);
        ReplayClockText.Text = FormatClock(SelectedTime(snapshot.Timeline));
        ReplayEndClockText.Text = FormatClock(SelectedEndTime());
        LogicalTimelineProgress.Value = clampedIndex;
        PhysicalTimelineProgress.Value = Math.Min(PhysicalTimelineProgress.Maximum, snapshot.Frame.Source.Index);
        EvidenceTimelineProgress.Value = diagnosticRows.Count(row =>
            row.Diagnostic.Location.LineNumber <= snapshot.Frame.Source.Location.LineNumber);

        synchronizingSelection = true;
        try
        {
            ReplaySeekSlider.Value = clampedIndex;
            var decodedRow = decodedRows[clampedIndex];
            DecodedFramesList.SelectedItem = decodedRow;
            DecodedFramesList.ScrollIntoView(decodedRow);
            if (rawRowsById.TryGetValue(snapshot.Frame.Source.StableId, out var rawRow))
            {
                RawRecordsList.SelectedItem = rawRow;
                RawRecordsList.ScrollIntoView(rawRow);
            }
        }
        finally
        {
            synchronizingSelection = false;
        }

        ShowRecord(snapshot.Frame.Source, snapshot.Frame);
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
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
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
            0 => ReplayPaintMode.HostState,
            1 => ReplayPaintMode.ReportedFrame,
            _ => ReplayPaintMode.Compare,
        };
        PaintSurface?.SetMode(mode);
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
        if (e.Key == Key.Space)
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
    }

    protected override void OnClosed(EventArgs e)
    {
        StopPlayback();
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
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

        foreach (var finger in frame.Fingers.Where(IsReportedFinger))
        {
            builder.AppendLine($"id {finger.Id,2}  {finger.Type,-8} {finger.Status,-8} x={finger.X,5} y={finger.Y,5}");
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

}
