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
using Nvt.Replay.Formats.Desay97;
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
    private CancellationTokenSource? playbackCancellation;
    private int currentLogicalIndex = -1;
    private int? loopIn;
    private int? loopOut;
    private double replaySpeed = 1;
    private bool maxReplaySpeed;
    private bool synchronizingSelection;
    private string? pendingSourcePath;
    private bool configuringSourceChoice;

    public MainWindow()
    {
        InitializeComponent();
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
            DiagnosticListBox.ItemsSource = diagnosticRows;
            DiagnosticCountText.Text = diagnosticRows.Length.ToString(CultureInfo.InvariantCulture);
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
            ConfigurationHintText.Text = "Select the operator-confirmed version; source detection does not infer it.";
            TimelineSummaryText.Text = $"{session.Records.Count:N0} physical · 0 logical · {diagnosticRows.Length:N0} evidence";
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

    private async void DecodeButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null)
        {
            return;
        }

        if (EventVersionComboBox.SelectedItem is not ComboBoxItem selected ||
            selected.Content is not string versionText)
        {
            ConfigurationHintText.Text = "Event Buffer Version is required and must be confirmed by the operator.";
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

            DecodedFramesList.ItemsSource = decodedRows;
            DiagnosticListBox.ItemsSource = diagnosticRows;
            DiagnosticCountText.Text = diagnosticRows.Length.ToString(CultureInfo.InvariantCulture);
            SessionStatusText.Text = $"{formatLabel} · {decodedRows.Length:N0} decoded frames · {diagnosticRows.Length:N0} diagnostics";
            ConfigurationHintText.Text = $"{formatLabel} selected manually · raw source remains unchanged";
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
                ? "Desay 0x97 requires Standard or Benz Palm; the application never auto-detects it."
                : "Select the operator-confirmed version; source detection does not infer it.";
        }
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
        currentLogicalIndex = -1;
        loopIn = null;
        loopOut = null;
        RawRecordsList.ItemsSource = null;
        DecodedFramesList.ItemsSource = null;
        DiagnosticListBox.ItemsSource = null;
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
        Desay97ProfileComboBox.IsEnabled = !busy && session is not null;
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
        PaintSurface.SetExtent(replaySession.AllReportedContacts);
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
        foreach (var finger in frame.Fingers)
        {
            var semantic = finger.Invalid ? "Invalid" : finger.Palm ? "Palm" : "Finger";
            builder.AppendLine($"id {finger.Id,2}  {semantic,-8} {finger.Status,-8} x={finger.X,5} y={finger.Y,5}");
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
