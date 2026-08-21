using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

/// <summary>
/// Approved, stateful workspace examples that complement the base Paint/Output/Heatmap/Settings matrix.
/// Each capture exercises real UI wiring so the snapshots cannot silently drift into a decorative mock state.
/// </summary>
public sealed class AdvancedWorkspaceSnapshotTests
{
    [AvaloniaFact]
    public async Task Review_marker_selected_keeps_queue_and_marker_actions_legible()
    {
        var window = ShowWindow(1920, 1080, ThemeVariant.Dark);
        try
        {
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            RaiseClick(Required<Button>(window, "AddMarkerButton"));

            var markerName = Required<TextBox>(window, "MarkerNameTextBox");
            markerName.Text = "3-finger convergence";
            RaiseClick(Required<Button>(window, "RenameMarkerButton"));
            Required<TabControl>(window, "InspectorDetailsTabs").SelectedItem =
                Required<TabItem>(window, "InspectorReviewTab");
            ExpandReviewRail(window);
            Stabilize(window);

            Assert.Equal("3-finger convergence", markerName.Text);
            Assert.True(Required<StackPanel>(window, "ReviewActionsPanel").IsVisible);
            Assert.True(Required<StackPanel>(window, "AnnotationMetadataPanel").IsVisible);
            Assert.True(Required<Button>(window, "RenameMarkerButton").IsVisible);
            Assert.True(Required<Button>(window, "UnmarkButton").IsVisible);
            Assert.True(Required<Button>(window, "ClearMarkersButton").IsEnabled);
            Assert.False(Required<WrapPanel>(window, "FindingWorkflowActionsPanel").IsVisible);
            Assert.False(Required<ComboBox>(window, "ReviewOccurrenceComboBox").IsVisible);
            Assert.Equal("Frame 7 · source line 510", Required<TextBlock>(window, "MarkerOccurrenceText").Text);
            Assert.False(Required<TextBlock>(window, "ReviewStateText").IsVisible);
            Assert.Single(Assert.IsType<ReviewInspectorWorkspace>(window.ReviewWorkspace).Markers);
            Assert.True(markerName.FontSize >= 13);
            AssertInside(Required<Button>(window, "ClearMarkersButton"), Required<Border>(window, "ReviewRailBorder"));
            AssertInside(Required<Button>(window, "RenameMarkerButton"), Required<Grid>(window, "InspectorRailContent"));
            AssertInside(Required<Button>(window, "UnmarkButton"), Required<Grid>(window, "InspectorRailContent"));

            VisualTestCapture.ProcessSnapshot(window, "review-marker-selected-1920x1080-dark.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_alarm_light_keeps_failure_semantics_and_navigation_contrast()
    {
        var capture = await WriteKingstVisCaptureAsync(
            "inspector-alarm.csv",
            CommonPacket(asil: 0x02, corruptCrc: true, x: 120),
            CommonPacket(asil: 0x02, corruptCrc: true, x: 240));
        var window = ShowWindow(1920, 1080, ThemeVariant.Light);
        try
        {
            await window.OpenCaptureAsync(capture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Required<ListBox>(window, "DecodedFramesList").SelectedIndex = 0;
            Stabilize(window);

            var crc = Required<TextBlock>(window, "InspectorCrcText");
            var asil = Required<TextBlock>(window, "InspectorAsilText");
            var alert = Required<Border>(window, "InspectorAlertBorder");
            var previous = Required<Button>(window, "PreviousFindingButton");
            var next = Required<Button>(window, "NextFindingButton");
            Assert.Equal("FAIL", crc.Text);
            Assert.Equal("ALARM", asil.Text);
            Assert.Contains("healthTextAlarm", crc.Classes);
            Assert.Contains("healthTextAlarm", asil.Classes);
            Assert.True(alert.IsVisible);
            Assert.True(previous.IsVisible);
            Assert.True(next.IsVisible);
            Assert.Equal("COMMON_CRC_MISMATCH", EngineeringDisplayText.RemoveBreakOpportunities(Required<TextBlock>(window, "InspectorAlertText").Text!));
            Assert.Contains("_\u200B", Required<TextBlock>(window, "InspectorAlertText").Text!);
            Assert.True(Required<TextBlock>(window, "InspectorAlertText").FontSize >= 12.5);
            AssertInside(previous, alert);
            AssertInside(next, alert);

            VisualTestCapture.ProcessSnapshot(window, "inspector-alarm-1920x1080-light.png");
        }
        finally
        {
            window.Close();
            File.Delete(capture);
        }
    }

    [AvaloniaFact]
    public async Task Inspector_ten_contacts_narrow_keeps_id_ten_selected_and_in_bounds()
    {
        var capture = await WriteKingstVisCaptureAsync("inspector-ten-contacts.csv", MixedTenContactPacket());
        var window = ShowWindow(1180, 720, ThemeVariant.Dark);
        try
        {
            await window.OpenCaptureAsync(capture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            ExpandInspectorRail(window);

            var contacts = Required<ListBox>(window, "InspectorContactsList");
            contacts.SelectedIndex = 9;
            contacts.ScrollIntoView(contacts.SelectedItem!);
            Stabilize(window);

            var selected = Assert.IsType<InspectorContactRow>(contacts.SelectedItem);
            Assert.Equal((byte)10, selected.Id);
            Assert.Equal((byte)10, Required<ReplayPaintSurface>(window, "PaintSurface").HighlightedContactId);
            Assert.Equal("8", Required<TextBlock>(window, "InspectorFingerText").Text);
            Assert.Equal("1", Required<TextBlock>(window, "InspectorGloveText").Text);
            Assert.Equal("1", Required<TextBlock>(window, "InspectorPalmText").Text);
            Assert.Equal("CAPTURE_END_ACTIVE_CONTACTS", EngineeringDisplayText.RemoveBreakOpportunities(Required<TextBlock>(window, "InspectorAlertText").Text!));
            var selectedContainer = contacts.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Single(item => item.DataContext is InspectorContactRow { Id: 10 });
            AssertInside(selectedContainer, contacts);

            VisualTestCapture.ProcessSnapshot(window, "inspector-ten-contacts-1180x720-dark.png");
        }
        finally
        {
            window.Close();
            File.Delete(capture);
        }
    }

    [AvaloniaFact]
    public async Task Inspector_raw_register_keeps_projected_meaning_and_source_identity_visible()
    {
        var window = ShowWindow(1920, 1080, ThemeVariant.Dark);
        try
        {
            await window.OpenCaptureAsync(Fixture("nds-register-map.nds.txt"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null, registerProfile: "51927");

            var records = Required<ListBox>(window, "RawRecordsList");
            records.SelectedItem = records.Items.OfType<RawRecordRow>()
                .First(row => row.Register.Contains("FW State", StringComparison.OrdinalIgnoreCase));
            Required<TabControl>(window, "InspectorDetailsTabs").SelectedItem =
                Required<TabItem>(window, "InspectorRawTab");
            Required<Expander>(window, "SourceIdentityExpander").IsExpanded = true;
            Stabilize(window);

            Assert.Equal("Register", Required<TextBlock>(window, "InspectorTitleText").Text);
            Assert.Contains("FW State", Required<TextBlock>(window, "InspectorSubtitleText").Text);
            var sourceFields = EngineeringDisplayText.RemoveBreakOpportunities(Required<TextBlock>(window, "SourceFieldsText").Text!);
            Assert.Contains("register_readable", sourceFields);
            Assert.Contains("FW State · Normal Run", sourceFields);
            Assert.Contains("register_profile_resolution=\n  resolved", sourceFields);
            Assert.NotEqual("-", Required<TextBlock>(window, "StableIdText").Text);
            Assert.True(Required<Expander>(window, "SourceIdentityExpander").IsExpanded);
            Assert.True(Required<TextBlock>(window, "SourceFieldsText").FontSize >= 12.5);
            AssertInside(Required<Expander>(window, "SourceIdentityExpander"), Required<Grid>(window, "InspectorRailContent"));

            VisualTestCapture.ProcessSnapshot(window, "inspector-raw-register-1920x1080-dark.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Raw_event_expansion_keeps_summary_dense_and_reveals_transport_evidence()
    {
        var window = ShowWindow(1920, 1080, ThemeVariant.Dark);
        try
        {
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null, registerProfile: "51927");

            Required<TabControl>(window, "WorkspaceTabs").SelectedIndex = 0;
            var records = Required<ListBox>(window, "RawRecordsList");
            var row = records.Items.OfType<RawRecordRow>()
                .First(item => item.Record.Operation == Nvt.Replay.Core.BusOperation.Read);
            records.SelectedItem = row;
            row.SetExpanded(true);
            records.ScrollIntoView(row);
            Stabilize(window);

            Assert.True(row.IsExpanded);
            Assert.NotNull(row.Detail);
            Assert.Contains("0010", row.Detail.FullPayload, StringComparison.Ordinal);
            var detailPanel = Assert.Single(
                records.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("rawRecordDetails") && border.IsVisible);

            var sectionHeaders = detailPanel.GetVisualDescendants()
                .OfType<TextBlock>()
                .Where(block => block.Classes.Contains("section"))
                .ToDictionary(block => block.Text!, StringComparer.Ordinal);
            var transactionX = sectionHeaders["TRANSACTION"].TranslatePoint(default, window)!.Value.X;
            var payloadX = sectionHeaders["FULL PAYLOAD"].TranslatePoint(default, window)!.Value.X;
            var sourceEvidenceX = sectionHeaders["SOURCE EVIDENCE"].TranslatePoint(default, window)!.Value.X;
            var sourceLineX = sectionHeaders["ORIGINAL SOURCE LINE"].TranslatePoint(default, window)!.Value.X;
            Assert.InRange(Math.Abs(transactionX - payloadX), 0, 0.5);
            Assert.InRange(Math.Abs(sourceEvidenceX - sourceLineX), 0, 0.5);

            VisualTestCapture.ProcessSnapshot(window, "raw-event-expanded-1920x1080-dark.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Paint_marker_quick_navigation_stays_compact_and_actionable()
    {
        var window = ShowWindow(1920, 1080, ThemeVariant.Dark);
        try
        {
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            RaiseClick(Required<Button>(window, "AddMarkerButton"));
            for (var index = 0; index < 6; index++)
                RaiseClick(Required<Button>(window, "NextFrameButton"));
            RaiseClick(Required<Button>(window, "AddMarkerButton"));
            Stabilize(window);

            var rail = Required<Border>(window, "PaintMarkerRailBorder");
            var rows = Required<ListBox>(window, "PaintMarkerListBox").Items.OfType<PaintMarkerRow>().ToArray();
            Assert.True(rail.IsVisible);
            Assert.False(Required<Border>(window, "ReviewRailBorder").IsVisible);
            Assert.Equal(2, rows.Length);
            Assert.Equal(["Frame 1", "Frame 7"], rows.Select(row => row.FrameLabel));
            Assert.Equal(["00:00.000", "00:00.600"], rows.Select(row => row.TimeLabel));
            Assert.Equal(212, rail.Bounds.Width);
            AssertInside(Required<ListBox>(window, "PaintMarkerListBox"), rail);

            VisualTestCapture.ProcessSnapshot(window, "paint-markers-1920x1080-dark.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Output_export_progress_uses_the_info_rail_without_reducing_preview_controls()
    {
        var window = ShowWindow(1920, 1080, ThemeVariant.Dark);
        ExportJobHandle? handle = null;
        try
        {
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => window.OutputPreviewPlanIdentity is not null);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handle = window.StartOutputExportJob(async (cancellationToken, progress) =>
            {
                progress.Report(new ReplayExportProgress(46, 120, "Rendering and encoding MP4"));
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            });
            await started.Task;
            await WaitUntilAsync(() => Required<Border>(window, "OutputExportActivityPanel").IsVisible);
            Stabilize(window);

            var info = Required<Border>(window, "OutputInfoPanel");
            AssertInside(Required<Border>(window, "OutputExportActivityPanel"), info);
            AssertInside(Required<Button>(window, "OutputExportCancelButton"), info);
            Assert.False(Required<Button>(window, "ExportSelectedOutputButton").IsVisible);
            Assert.Equal("\uE711", Required<Button>(window, "OutputExportCancelButton").Content);
            Assert.True(Required<ReplayVideoPreviewSurface>(window, "OutputVideoPreview").Bounds.Width > info.Bounds.Width * 3);

            // The Fluent progress indicator intentionally animates its highlight.
            // Keep this state under semantic layout assertions instead of an exact-pixel gate.
        }
        finally
        {
            if (handle is not null)
            {
                window.CancelOutputExportJob();
                await handle.Completion;
            }
            window.Close();
        }
    }

    private static string Fixture(string fileName) => Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);

    private static MainWindow ShowWindow(double width, double height, ThemeVariant theme)
    {
        if (Application.Current is { } application) application.RequestedThemeVariant = theme;
        var window = new MainWindow
        {
            Width = width,
            Height = height,
            WindowState = WindowState.Normal,
        };
        window.Show();
        Stabilize(window);
        return window;
    }

    private static void ExpandReviewRail(MainWindow window)
    {
        if (Required<Border>(window, "ReviewRailBorder").IsVisible) return;
        RaiseClick(Required<Button>(window, "ReviewRailToggleButton"));
    }

    private static void ExpandInspectorRail(MainWindow window)
    {
        if (Required<Grid>(window, "InspectorRailContent").IsVisible) return;
        RaiseClick(Required<Button>(window, "InspectorRailToggleButton"));
    }

    private static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Stabilize(Window window)
    {
        window.MouseMove(new Point(4, 88));
        Dispatcher.UIThread.RunJobs();
        VisualTestCapture.Stabilize(window);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("The expected UI state did not become available.");
    }

    private static void AssertInside(Control control, Visual ancestor)
    {
        Assert.True(control.IsVisible, $"{control.Name ?? control.GetType().Name} is not visible.");
        var origin = control.TranslatePoint(default, ancestor) ??
            throw new InvalidOperationException($"{control.Name ?? control.GetType().Name} could not translate into its ancestor.");
        var bounds = new Rect(origin, control.Bounds.Size);
        Assert.True(bounds.Left >= -0.5, $"{control.Name} is clipped on the left: {bounds}.");
        Assert.True(bounds.Top >= -0.5, $"{control.Name} is clipped on the top: {bounds}.");
        Assert.True(bounds.Right <= ancestor.Bounds.Width + 0.5, $"{control.Name} is clipped on the right: {bounds} / {ancestor.Bounds}.");
        Assert.True(bounds.Bottom <= ancestor.Bounds.Height + 0.5, $"{control.Name} is clipped on the bottom: {bounds} / {ancestor.Bounds}.");
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control '{name}'.");

    private static byte[] CommonPacket(byte asil, bool corruptCrc, ushort x)
    {
        var packet = new byte[CommonEventBufferDecoder.FrameLength];
        packet.AsSpan(1, CommonEventBufferDecoder.FingerCount * CommonEventBufferDecoder.FingerLength).Fill(0xFF);
        packet[0] = 1;
        packet[1] = (byte)((1 << 4) | (byte)Nvt.Replay.Core.TouchStatus.Move);
        packet[2] = (byte)(x >> 8);
        packet[3] = (byte)x;
        packet[4] = 0x01;
        packet[5] = 0x40;
        packet[6] = 0xFF;
        packet[7] = 0xFF;
        packet.AsSpan(71, 2).Fill(0xFF);
        packet[73] = asil;
        packet[79] = CommonCrc8.Compute(packet.AsSpan(0, 79));
        if (corruptCrc) packet[79] ^= 0xFF;
        return packet;
    }

    private static byte[] MixedTenContactPacket()
    {
        var packet = new byte[CommonEventBufferDecoder.FrameLength];
        packet[0] = CommonEventBufferDecoder.FingerCount;
        for (var index = 0; index < CommonEventBufferDecoder.FingerCount; index++)
        {
            var offset = 1 + (index * CommonEventBufferDecoder.FingerLength);
            var id = (byte)(index + 1);
            var type = index switch
            {
                8 => Nvt.Replay.Core.TouchType.Glove,
                9 => Nvt.Replay.Core.TouchType.Palm,
                _ => Nvt.Replay.Core.TouchType.Finger,
            };
            var x = (ushort)(140 + (index * 170));
            var y = (ushort)(100 + (index * 80));
            packet[offset] = (byte)((id << 4) | ((byte)type << 2) | (byte)Nvt.Replay.Core.TouchStatus.Move);
            packet[offset + 1] = (byte)(x >> 8);
            packet[offset + 2] = (byte)x;
            packet[offset + 3] = (byte)(y >> 8);
            packet[offset + 4] = (byte)y;
            packet[offset + 5] = 0xFF;
            packet[offset + 6] = 0xFF;
        }
        packet[71] = 0xFF;
        packet[72] = 0xFF;
        packet[73] = 0x00;
        packet.AsSpan(74, 5).Fill(0xFF);
        packet[79] = CommonCrc8.Compute(packet.AsSpan(0, 79));
        return packet;
    }

    private static async Task<string> WriteKingstVisCaptureAsync(string fileName, params byte[][] packets)
    {
        var directory = Path.Combine(Path.GetTempPath(), "nvt-replay-advanced-snapshots");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var lines = new List<string> { "Time [s],Packet ID,Address,Data,Read/Write,ACK" };
        var packetId = 0;
        for (var frameIndex = 0; frameIndex < packets.Length; frameIndex++)
        {
            var writeTime = 1.0 + (frameIndex * 0.1);
            foreach (var value in new byte[] { 0xFF, 0x09, 0x90, 0x00 })
                lines.Add($"{writeTime.ToString("0.000000", CultureInfo.InvariantCulture)},{packetId},0x02,0x{value:X2},Write,ACK");
            packetId++;
            var readTime = writeTime + 0.0002;
            for (var index = 0; index < packets[frameIndex].Length; index++)
            {
                var ack = index == packets[frameIndex].Length - 1 ? "NAK" : "ACK";
                lines.Add($"{readTime.ToString("0.000000", CultureInfo.InvariantCulture)},{packetId},0x03,0x{packets[frameIndex][index]:X2},Read,{ack}");
            }
            packetId++;
        }
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }
}
