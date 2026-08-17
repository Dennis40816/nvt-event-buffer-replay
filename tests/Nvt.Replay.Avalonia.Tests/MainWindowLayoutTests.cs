using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
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
            Assert.Equal(new GridLength(320), shell.ColumnDefinitions[2].Width);
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

            application.RequestedThemeVariant = ThemeVariant.Dark;
            var paintDark = CaptureHash(window, "03-golden-paint-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            var paintLight = CaptureHash(window, "04-golden-paint-light.png");

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
            var outputLight = CaptureHash(window, "05-golden-output-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            var outputDark = CaptureHash(window, "06-golden-output-dark.png");

            Assert.NotEqual(paintDark, paintLight);
            Assert.NotEqual(outputDark, outputLight);
            Assert.NotEqual(paintDark, outputDark);
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
