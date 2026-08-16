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
            var initialVersion = GetOption(desktop.Args, "--event-version");
            var initialPalmProfile = GetOption(desktop.Args, "--palm-profile");
            var initialRegisterProfile = GetOption(desktop.Args, "--register-profile");
            if (initialCapture is not null)
            {
                window.Opened += async (_, _) =>
                {
                    await window.OpenCaptureAsync(initialCapture);
                    if (initialVersion is not null)
                        await window.ApplyStartupDecodeAsync(initialVersion, initialPalmProfile, initialRegisterProfile);
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? GetOption(IReadOnlyList<string>? args, string name)
    {
        if (args is null) return null;
        for (var index = 0; index < args.Count - 1; index++)
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }
}
