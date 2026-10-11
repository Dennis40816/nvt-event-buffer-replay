using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Nvt.Replay.Avalonia;

public partial class App : Application
{
    // UI thread only; subscribed once and released when the desktop lifetime exits.
    private UiExceptionHooks? _exceptionHooks;
    internal bool ExceptionHandlersRegistered => _exceptionHooks is not null;

    public override void Initialize()
    {
        _exceptionHooks ??= UiExceptionHooks.Register(() =>
            (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is MainWindow window
                ? window.FailureReporter : null);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            this.AttachDeveloperTools();
#endif
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.Exit += (_, _) =>
            {
                _exceptionHooks?.Dispose();
                _exceptionHooks = null;
            };
            var initialCapture = desktop.Args?.FirstOrDefault(File.Exists);
            var initialVersion = GetOption(desktop.Args, "--event-version");
            var initialPalmProfile = GetOption(desktop.Args, "--palm-profile");
            var initialRegisterProfile = GetOption(desktop.Args, "--register-profile");
            if (initialCapture is not null)
            {
                window.Opened += (_, _) => window.FailureReporter.Runner.Run("capture.startup",
                    _ => OpenInitialCaptureAsync(window, initialCapture, initialVersion, initialPalmProfile, initialRegisterProfile),
                    CancellationToken.None);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task OpenInitialCaptureAsync(
        MainWindow window, string path, string? version, string? palmProfile, string? registerProfile)
    {
        await window.OpenCaptureAsync(path, promptForConfiguration: version is null);
        if (version is not null)
            await window.ApplyStartupDecodeAsync(version, palmProfile, registerProfile);
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
