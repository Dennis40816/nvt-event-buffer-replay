using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private CancellationTokenSource? operationCancellation;
    private NdsCaptureSession? session;
    private CommonInspectionReport? report;
    private Dictionary<string, RawRecordRow> rawRowsById = [];

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
            var decodedRows = await Task.Run(
                () => report.Frames.Select(frame => new DecodedFrameRow(frame)).ToArray(),
                cancellationToken);
            var diagnosticRows = await Task.Run(
                () => report.Diagnostics.Select(diagnostic => new DiagnosticRow(diagnostic)).ToArray(),
                cancellationToken);

            DecodedFramesList.ItemsSource = decodedRows;
            DiagnosticListBox.ItemsSource = diagnosticRows;
            DiagnosticCountText.Text = diagnosticRows.Length.ToString(CultureInfo.InvariantCulture);
            SessionStatusText.Text = $"Common {versionText} · {decodedRows.Length:N0} decoded frames · {diagnosticRows.Length:N0} diagnostics";
            ConfigurationHintText.Text = $"Common {versionText} selected manually · raw source remains unchanged";
            TimelineSummaryText.Text = $"{session.Records.Count:N0} physical · {decodedRows.Length:N0} logical · {diagnosticRows.Length:N0} evidence";
            TimelineStatusText.Text = "Decoded events are available; replay controls arrive in the next slice";
            WorkspaceTabs.SelectedIndex = 1;
            if (decodedRows.Length > 0)
            {
                DecodedFramesList.SelectedIndex = 0;
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
        }
    }

    private void DecodedFramesList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DecodedFramesList.SelectedItem is not DecodedFrameRow row)
        {
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
        session = null;
        report = null;
        rawRowsById = [];
        RawRecordsList.ItemsSource = null;
        DecodedFramesList.ItemsSource = null;
        DiagnosticListBox.ItemsSource = null;
        EventVersionComboBox.SelectedIndex = -1;
        EventVersionComboBox.IsEnabled = false;
        DecodeButton.IsEnabled = false;
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
        !(finger.RawMeta == 0xFF && finger.X == 0xFFFF && finger.Y == 0xFFFF && finger.Reserved.All(value => value == 0xFF)) &&
        (finger.Status != TouchStatus.NoFinger || finger.Type == TouchType.Palm);

    private static string VersionText(CommonEventBufferVersion version) => version switch
    {
        CommonEventBufferVersion.V82 => "0x82",
        CommonEventBufferVersion.V83 => "0x83",
        CommonEventBufferVersion.V84 => "0x84",
        CommonEventBufferVersion.V85 => "0x85",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

}
