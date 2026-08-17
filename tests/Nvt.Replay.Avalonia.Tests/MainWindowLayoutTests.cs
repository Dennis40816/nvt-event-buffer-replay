using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
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

            Assert.Null(window.FindControl<Button>("DecodeButton"));
            Assert.Equal(34, version.Bounds.Height);
            Assert.Equal(34, profile.Bounds.Height);
            Assert.Equal(34, panelWidth.Height);
            Assert.Equal(36, load.Bounds.Height);
            Assert.Equal(VerticalAlignment.Center, version.VerticalAlignment);
            Assert.Equal(VerticalAlignment.Center, panelWidth.VerticalAlignment);
            Assert.Equal(VerticalAlignment.Center, load.VerticalContentAlignment);
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
            var dark = CaptureHash(window);
            application.RequestedThemeVariant = ThemeVariant.Light;
            var light = CaptureHash(window);

            Assert.NotEqual(dark, light);
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

    private static string CaptureHash(Window window)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer returned no frame.");
        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
