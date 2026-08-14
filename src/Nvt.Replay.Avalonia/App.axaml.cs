using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Nvt.Replay.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var initialCapture = desktop.Args?.FirstOrDefault(File.Exists);
            if (initialCapture is not null)
            {
                window.Opened += async (_, _) => await window.OpenCaptureAsync(initialCapture);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
