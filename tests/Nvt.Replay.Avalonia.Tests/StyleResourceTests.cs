using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nvt.Replay.Avalonia;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class StyleResourceTests
{
    [AvaloniaFact]
    public void Main_window_loads_external_style_dictionaries()
    {
        var dictionaryFiles = new[]
        {
            "MainWindow.TypographyButtons.axaml",
            "MainWindow.FieldsTransport.axaml",
            "MainWindow.Canvas.axaml",
            "MainWindow.Shared.axaml",
            "MainWindow.Inspector.axaml",
            "MainWindow.WorkspacePanels.axaml",
            "MainWindow.Output.axaml",
            "MainWindow.InspectorDisclosure.axaml"
        };

        foreach (var fileName in dictionaryFiles)
        {
            var uri = new Uri($"avares://Nvt.Replay.Avalonia/Styles/{fileName}");
            var styles = Assert.IsType<Styles>(AvaloniaXamlLoader.Load(uri, baseUri: null));
            Assert.NotEmpty(styles);
        }

        var window = new MainWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal(32, Assert.IsType<TextBox>(window.FindControl<TextBox>("PanelWidthTextBox")).Height);
            Assert.Equal(32, Assert.IsType<Button>(window.FindControl<Button>("SaveReviewButton")).Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Core_tokens_and_roles_resolve_and_follow_both_theme_variants(bool light)
    {
        var window = new MainWindow { RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Classes.Add("reducedMotion");
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            AssertPalette(window, light);
            var load = Assert.IsType<Button>(window.FindControl<Button>("LoadButton"));
            var save = Assert.IsType<Button>(window.FindControl<Button>("SaveReviewButton"));
            var icon = Assert.IsType<Button>(window.FindControl<Button>("PreviousFrameButton"));
            var selected = Assert.IsType<ToggleButton>(window.FindControl<ToggleButton>("TraceToggleButton"));
            Assert.Contains("actionPrimary", load.Classes);
            Assert.Contains("actionGhost", save.Classes);
            Assert.Contains("actionIconButton", icon.Classes);
            Assert.Contains("chipAction", selected.Classes);
            foreach (var button in new Button[] { load, save, icon, selected })
            {
                Assert.Equal(32, button.Height);
                Assert.Equal(32, button.MinHeight);
                Assert.Equal(new CornerRadius(999), button.CornerRadius);
            }
            Assert.Equal(new Thickness(14, 0), load.Padding);
            Assert.Equal(32, icon.Width);
            Assert.Equal(new Thickness(0), icon.Padding);
            AssertBrush(window, "NfcAccentBrush", load.Background);
            AssertBrush(window, "Nvt.Button.PrimaryLabelBrush", load.Foreground);
            AssertBrush(window, "NfcSurfaceSubtleBrush", save.Background);
            AssertBrush(window, "NfcBorderMutedBrush", save.BorderBrush);
            AssertBrush(window, "NfcTextDisabledBrush", save.Foreground);

            window.RequestedThemeVariant = light ? ThemeVariant.Dark : ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            AssertPalette(window, !light);
            AssertBrush(window, "NfcAccentBrush", load.Background);
            AssertBrush(window, "Nvt.Button.PrimaryLabelBrush", load.Foreground);
            AssertBrush(window, "NfcSurfaceSubtleBrush", save.Background);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertPalette(Control owner, bool light)
    {
        var values = new (string Key, string Light, string Dark)[]
        {
            ("NfcAccentBorderBrush", "#4A6F00", "#82B11A"),
            ("NfcAccentBorderLightBrush", "#4A6F00", "#82B11A"),
            ("NfcAccentBorderStrongBrush", "#3D5B00", "#97CD1E"),
            ("NfcAccentBrush", "#4A6F00", "#82B11A"),
            ("NfcAccentStrongBrush", "#3D5B00", "#97CD1E"),
            ("NfcAccentSurfaceBrush", "#F2F5ED", "#1F2A25"),
            ("NfcAccentSurfaceSubtleBrush", "#F9FAF6", "#182126"),
            ("NfcAppBackgroundBrush", "#F1F5F9", "#0B1220"),
            ("NfcBorderBrush", "#718096", "#708198"),
            ("NfcBorderMutedBrush", "#CBD5E1", "#334155"),
            ("NfcBorderSoftBrush", "#94A3B8", "#475569"),
            ("NfcDangerTextBrush", "#A82035", "#FFADB7"),
            ("NfcInfoTextBrush", "#245B91", "#8AC5F2"),
            ("NfcModalScrimBrush", "#660F172A", "#B30B1220"),
            ("NfcSecondaryActionPressedBrush", "#E2E8F0", "#243247"),
            ("NfcSelectionSurfaceBrush", "#E8EEF5", "#1E293B"),
            ("NfcSurfaceBrush", "#FFFFFF", "#111827"),
            ("NfcSurfaceSubtleBrush", "#F8FAFC", "#182337"),
            ("NfcTextBrush", "#1E293B", "#E2E8F0"),
            ("NfcTextDisabledBrush", "#68778C", "#7B8CA5"),
            ("NfcTextMutedBrush", "#526176", "#A1AEC2"),
            ("NfcTextSecondaryBrush", "#475569", "#CBD5E1"),
            ("NfcWarningSurfaceBrush", "#FFFBEB", "#2D2313"),
            ("NfcWarningTextBrush", "#875400", "#F5CE8A"),
            ("Nvt.Button.PrimaryLabelBrush", "#FFFFFF", "#0B1220"),
            ("Nvt.Focus.DangerRingBrush", "#C62828", "#FF6B6B"),
            ("Nvt.Focus.RingBrush", "#1F6FD1", "#4DA3FF")
        };
        foreach (var value in values)
        {
            Assert.True(owner.TryFindResource(value.Key, owner.ActualThemeVariant, out var resource));
            Assert.Equal(Color.Parse(light ? value.Light : value.Dark), Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color);
        }
    }

    private static void AssertBrush(Control owner, string key, IBrush? actual)
    {
        Assert.True(owner.TryFindResource(key, owner.ActualThemeVariant, out var resource));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color, Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }
}
