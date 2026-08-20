using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
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
            Assert.Equal(34, Assert.IsType<TextBox>(window.FindControl<TextBox>("PanelWidthTextBox")).Height);
            Assert.Equal(32, Assert.IsType<Button>(window.FindControl<Button>("SaveReviewButton")).Height);
        }
        finally
        {
            window.Close();
        }
    }
}
