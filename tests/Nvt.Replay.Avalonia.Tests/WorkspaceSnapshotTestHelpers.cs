using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

internal static class WorkspaceSnapshotTestHelpers
{
    internal static string Fixture(string fileName) => Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);

    internal static MainWindow ShowWindow(double width, double height, ThemeVariant theme)
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

    internal static void ExpandReviewRail(MainWindow window)
    {
        if (Required<Border>(window, "ReviewRailBorder").IsVisible) return;
        RaiseClick(Required<Button>(window, "ReviewRailToggleButton"));
    }

    internal static void ExpandInspectorRail(MainWindow window)
    {
        if (Required<Grid>(window, "InspectorRailContent").IsVisible) return;
        RaiseClick(Required<Button>(window, "InspectorRailToggleButton"));
    }

    internal static void RaiseClick(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    internal static void Stabilize(Window window)
    {
        window.MouseMove(new Point(4, 88));
        Dispatcher.UIThread.RunJobs();
        VisualTestCapture.Stabilize(window);
    }

    internal static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (predicate()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("The expected UI state did not become available.");
    }

    internal static void AssertInside(Control control, Visual ancestor)
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

    internal static T Required<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control '{name}'.");
}
