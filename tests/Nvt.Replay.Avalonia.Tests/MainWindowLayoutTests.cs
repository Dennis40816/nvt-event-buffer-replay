using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task Confirming_a_common_version_decodes_immediately_without_an_action_button()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");

            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var frames = Required<ListBox>(window, "DecodedFramesList");
            var hint = Required<TextBlock>(window, "ConfigurationHintText");
            var version = Required<ComboBox>(window, "EventVersionComboBox");
            Assert.Equal(1, version.SelectedIndex);
            Assert.Equal(19, frames.ItemCount);
            Assert.Contains("decoded automatically", hint.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Null(window.FindControl<Button>("DecodeButton"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Decode_configuration_is_automatic_compact_and_vertically_centered()
    {
        var window = ShowWindow();
        try
        {
            var version = Required<ComboBox>(window, "EventVersionComboBox");
            var profile = Required<ComboBox>(window, "RegisterProfileComboBox");
            var panelWidth = Required<TextBox>(window, "PanelWidthTextBox");
            var load = Required<Button>(window, "LoadButton");
            var save = Required<Button>(window, "SaveReviewButton");

            Assert.Null(window.FindControl<Button>("DecodeButton"));
            Assert.Equal("Select", version.PlaceholderText);
            Assert.Equal(34, version.Bounds.Height);
            Assert.Equal(34, profile.Bounds.Height);
            Assert.Equal(34, panelWidth.Height);
            Assert.Equal(36, load.Bounds.Height);
            Assert.Equal(VerticalAlignment.Center, version.VerticalAlignment);
            Assert.Equal(VerticalAlignment.Center, panelWidth.VerticalAlignment);
            Assert.Equal(VerticalAlignment.Center, load.VerticalContentAlignment);
            Assert.Equal(
                global::Avalonia.Media.Colors.Transparent,
                Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(save.Background).Color);
            Assert.Equal(
                global::Avalonia.Media.Colors.Transparent,
                Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(save.BorderBrush).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Empty_review_queue_starts_collapsed_and_keeps_the_canvas_wide()
    {
        var window = ShowWindow();
        try
        {
            var shell = Required<Grid>(window, "WorkspaceShellGrid");
            var reviewContent = Required<Grid>(window, "ReviewRailContent");
            var inspectorContent = Required<Grid>(window, "InspectorRailContent");

            Assert.Equal(new GridLength(42), shell.ColumnDefinitions[0].Width);
            Assert.False(reviewContent.IsVisible);
            Assert.Equal(new GridLength(4), shell.ColumnDefinitions[2].Width);
            Assert.Equal(new GridLength(380), shell.ColumnDefinitions[3].Width);
            Assert.True(inspectorContent.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Main_workspace_renders_in_both_dark_and_light_themes()
    {
        var window = ShowWindow();
        try
        {
            var application = Assert.IsType<App>(Application.Current);

            application.RequestedThemeVariant = ThemeVariant.Dark;
            var dark = CaptureHash(window, "01-empty-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            var light = CaptureHash(window, "02-empty-light.png");

            Assert.NotEqual(dark, light);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Golden_capture_renders_distinct_Paint_and_exact_MP4_preview_themes()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            var application = Assert.IsType<App>(Application.Current);
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            Dispatcher.UIThread.RunJobs();
            Required<ListBox>(window, "InspectorContactsList").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            application.RequestedThemeVariant = ThemeVariant.Dark;
            var paintDark = CaptureHash(window, "03-golden-paint-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            var paintLight = CaptureHash(window, "04-golden-paint-light.png");
            Required<Button>(window, "InspectorRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var collapsedLight = CaptureHash(window, "07-golden-inspector-collapsed-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            var collapsedDark = CaptureHash(window, "08-golden-inspector-collapsed-dark.png");
            Required<Button>(window, "InspectorRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var tabs = Required<TabControl>(window, "WorkspaceTabs");
            tabs.SelectedItem = Required<TabItem>(window, "AnalysisTab");
            var previewFrame = Required<TextBlock>(window, "OutputPreviewFrameText");
            await WaitUntilAsync(() => !string.Equals(previewFrame.Text, "output 0/0", StringComparison.Ordinal));
            Assert.Contains("1280 × 720", Required<TextBlock>(window, "OutputVideoBadgeText").Text);
            Assert.False(Required<Button>(window, "PlayPauseButton").IsEnabled);
            Assert.True(Required<Button>(window, "OutputPreviewPlayPauseButton").IsEnabled);
            var sourceLines = Required<TextBlock>(window, "OutputSourceText").Text!.Split('\n');
            Assert.Equal(32, sourceLines[^2].Length);
            Assert.Equal(32, sourceLines[^1].Length);
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var outputLight = CaptureHash(window, "05-golden-output-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            var outputDark = CaptureHash(window, "06-golden-output-dark.png");

            var outputContent = Required<ComboBox>(window, "OutputContentComboBox");
            outputContent.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var heatmapDark = CaptureHash(window, "09-golden-heatmap-dark.png");
            outputContent.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var packageDark = CaptureHash(window, "10-golden-package-dark.png");
            outputContent.SelectedIndex = 0;
            Required<ToggleButton>(window, "OutputSettingsToggleButton").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var settingsDark = CaptureHash(window, "11-golden-output-settings-dark.png");
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 5;
            Required<ComboBox>(window, "OutputResolutionComboBox").SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            var customSettingsDark = CaptureHash(window, "15-golden-output-custom-settings-dark.png");
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 2;
            Required<ComboBox>(window, "OutputResolutionComboBox").SelectedIndex = 0;
            Required<ToggleButton>(window, "OutputSettingsToggleButton").IsChecked = false;
            window.Width = 1180;
            window.Height = 720;
            Dispatcher.UIThread.RunJobs();
            var compactOutputDark = CaptureHash(window, "12-golden-output-compact-dark.png");
            outputContent.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var compactHeatmapDark = CaptureHash(window, "13-golden-heatmap-compact-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var compactHeatmapLight = CaptureHash(window, "14-golden-heatmap-compact-light.png");

            Assert.NotEqual(paintDark, paintLight);
            Assert.NotEqual(collapsedDark, collapsedLight);
            Assert.NotEqual(outputDark, outputLight);
            Assert.NotEqual(paintDark, outputDark);
            Assert.NotEqual(outputDark, heatmapDark);
            Assert.NotEqual(heatmapDark, packageDark);
            Assert.NotEqual(outputDark, settingsDark);
            Assert.NotEqual(settingsDark, customSettingsDark);
            Assert.NotEqual(outputDark, compactOutputDark);
            Assert.NotEqual(compactHeatmapDark, compactHeatmapLight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_contacts_select_the_matching_canvas_contact_and_collapsed_summary_stays_useful()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            Dispatcher.UIThread.RunJobs();

            var contacts = Required<ListBox>(window, "InspectorContactsList");
            Assert.Equal(3, contacts.ItemCount);
            contacts.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var selected = Assert.IsType<InspectorContactRow>(contacts.SelectedItem);
            Assert.Equal(selected.Id, Required<ReplayPaintSurface>(window, "PaintSurface").HighlightedContactId);
            Assert.Equal("3 active", Required<TextBlock>(window, "InspectorContactCountText").Text);

            Required<Button>(window, "InspectorRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var shell = Required<Grid>(window, "WorkspaceShellGrid");
            Assert.Equal(new GridLength(58), shell.ColumnDefinitions[3].Width);
            Assert.True(Required<Grid>(window, "InspectorCollapsedSummary").IsVisible);
            Assert.False(Required<Grid>(window, "InspectorRailContent").IsVisible);
            Assert.Equal("3", Required<TextBlock>(window, "CollapsedFingerText").Text);
            Assert.Equal("ASIL\nNORM", Required<TextBlock>(window, "CollapsedAsilText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Inspector_width_is_bounded_and_restored_after_collapsing()
    {
        var window = ShowWindow();
        try
        {
            var shell = Required<Grid>(window, "WorkspaceShellGrid");
            var inspectorColumn = shell.ColumnDefinitions[3];
            Assert.Equal(320, inspectorColumn.MinWidth);
            Assert.Equal(520, inspectorColumn.MaxWidth);
            Assert.True(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);

            inspectorColumn.Width = new GridLength(500);
            Dispatcher.UIThread.RunJobs();
            var toggle = Required<Button>(window, "InspectorRailToggleButton");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new GridLength(58), inspectorColumn.Width);
            Assert.False(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new GridLength(500), inspectorColumn.Width);
            Assert.True(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Drop_downs_reserve_the_longest_visible_option_without_fixed_widths()
    {
        var window = ShowWindow();
        try
        {
            var names = new[]
            {
                "SourceAdapterComboBox",
                "EventVersionComboBox",
                "RegisterProfileComboBox",
                "Desay97ProfileComboBox",
                "RegisterFilterComboBox",
                "PaintModeComboBox",
                "TrailModeComboBox",
                "TrailLengthComboBox",
                "LegendPositionComboBox",
                "ReviewOccurrenceComboBox",
                "ClockModeComboBox",
                "ReplaySpeedComboBox",
                "OutputContentComboBox",
                "OutputClockComboBox",
                "OutputSpeedComboBox",
                "OutputFrameRateComboBox",
                "OutputResolutionComboBox",
            };

            foreach (var name in names)
            {
                var comboBox = Required<ComboBox>(window, name);
                Assert.True(double.IsNaN(comboBox.Width), $"{name} still has a fixed width.");
                Assert.True(
                    comboBox.MinWidth >= ComboBoxAutoSizer.RequiredWidth(comboBox),
                    $"{name} does not reserve its longest option.");
            }

            var legend = Required<ComboBox>(window, "LegendPositionComboBox");
            var selectedWidth = ComboBoxAutoSizer.MeasureLabelWidth(legend, "Auto");
            var longestWidth = ComboBoxAutoSizer.MeasureLabelWidth(legend, "Bottom right");
            Assert.True(longestWidth > selectedWidth);
            Assert.True(legend.MinWidth >= longestWidth + ComboBoxAutoSizer.ChromeAndPaddingWidth);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Output_selector_shows_one_primary_surface_and_video_settings_drive_preview_sampling()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputPreviewFrameText").Text != "output 0/0");

            var outputContent = Required<ComboBox>(window, "OutputContentComboBox");
            Assert.Equal(3, outputContent.ItemCount);
            Assert.True(Required<Grid>(window, "OutputVideoPanel").IsVisible);
            Assert.False(Required<Border>(window, "OutputHeatmapPanel").IsVisible);
            Assert.False(Required<Border>(window, "OutputPackagePanel").IsVisible);

            outputContent.SelectedIndex = 1;
            Assert.False(Required<Grid>(window, "OutputVideoPanel").IsVisible);
            Assert.True(Required<Border>(window, "OutputHeatmapPanel").IsVisible);
            Assert.Equal("Export PNG", Required<Button>(window, "ExportSelectedOutputButton").Content);

            outputContent.SelectedIndex = 0;
            var settings = Required<ToggleButton>(window, "OutputSettingsToggleButton");
            settings.IsChecked = true;
            Assert.True(Required<Border>(window, "OutputVideoSettingsPanel").IsVisible);
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 5;
            var customSpeed = Required<TextBox>(window, "OutputCustomSpeedTextBox");
            Assert.True(customSpeed.IsVisible);
            customSpeed.Text = "1.75";
            customSpeed.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputVideoBadgeText").Text?.Contains("1.75×", StringComparison.Ordinal) == true);

            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 3;
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputVideoBadgeText").Text?.Contains("2×", StringComparison.Ordinal) == true);
            Assert.Contains("2×", Required<TextBlock>(window, "OutputVideoBadgeText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Timeline_empty_space_click_seeks_to_the_nearest_logical_frame()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var timeline = Required<Control>(window, "ReplayTimelineSurface");
            var localPoint = new Point(timeline.Bounds.Width * 0.75, timeline.Bounds.Height * 0.32);
            var windowPoint = timeline.TranslatePoint(localPoint, window) ??
                throw new InvalidOperationException("Timeline could not translate its click point to the window.");
            var hit = window.InputHitTest(windowPoint);
            Assert.Same(timeline, hit);

            window.MouseDown(windowPoint, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(windowPoint, MouseButton.Left, RawInputModifiers.None);

            Assert.Equal("15 / 19", Required<TextBlock>(window, "InspectorLogicalText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_summarizes_per_byte_acknowledgements_without_losing_the_transport_data()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);

            var rawRecords = Required<ListBox>(window, "RawRecordsList");
            var eventBufferRead = rawRecords.Items
                .OfType<RawRecordRow>()
                .First(row => row.Record.I2c?.Acked.Count == 80);
            rawRecords.SelectedItem = eventBufferRead;
            Dispatcher.UIThread.RunJobs();

            var transport = Required<TextBlock>(window, "TransportText").Text ?? string.Empty;
            Assert.Contains("data ACKs=ACK ×79; final NAK", transport);
            Assert.DoesNotContain("ACK ACK ACK", transport);
            Assert.Equal(80, eventBufferRead.Record.I2c?.Acked.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Pending_0x97_configuration_names_the_still_active_replay()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            Required<ComboBox>(window, "EventVersionComboBox").SelectedIndex = 4;

            var hint = Required<TextBlock>(window, "ConfigurationHintText").Text ?? string.Empty;
            Assert.Contains("0x97 pending", hint);
            Assert.Contains("active replay: Common 0x83", hint);
            Assert.Equal("Physical #1 · Common 0x83 · Host-state", Required<TextBlock>(window, "InspectorSubtitleText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Theme_toggle_preserves_the_capture_status_summary()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var status = Required<TextBlock>(window, "SessionStatusText");
            var expected = status.Text;

            Required<Button>(window, "ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(expected, status.Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static MainWindow ShowWindow()
    {
        var window = new MainWindow { Width = 1480, Height = 840 };
        window.Show();
        return window;
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control '{name}'.");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Timed out while waiting for the output preview.");
    }

    private static string CaptureHash(Window window, string artifactName)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer returned no frame.");
        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);
        var artifactDirectory = Environment.GetEnvironmentVariable("NVT_UI_AUDIT_DIR");
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            Directory.CreateDirectory(artifactDirectory);
            frame.Save(Path.Combine(artifactDirectory, artifactName), PngBitmapEncoderOptions.Default);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
