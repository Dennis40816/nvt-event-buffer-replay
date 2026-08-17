using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Nvt.Replay.Avalonia;

[assembly: AvaloniaTestApplication(typeof(Nvt.Replay.Avalonia.Tests.TestAppBuilder))]

namespace Nvt.Replay.Avalonia.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}
