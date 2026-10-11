// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using static Nvt.Replay.Avalonia.Tests.WorkspaceSnapshotTestHelpers;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowFullscreenTests
{
    [AvaloniaTheory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Minimized)]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public Task LeavingFullscreenRestoresPreviousWindowState(WindowState previousState) =>
        WithOutputPreviewAsync(window =>
        {
            window.WindowState = previousState;
            RaiseClick(Required<Button>(window, "OutputPreviewFullscreenButton"));
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            Assert.True(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);

            RaiseClick(Required<Button>(window, "OutputFullscreenExitButton"));

            Assert.Equal(previousState, window.WindowState);
            Assert.False(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);
        });

    [AvaloniaFact]
    public Task LeavingFullscreenTwiceLeavesCurrentWindowStateUnchanged() =>
        WithOutputPreviewAsync(window =>
        {
            window.WindowState = WindowState.Maximized;
            RaiseClick(Required<Button>(window, "OutputPreviewFullscreenButton"));
            RaiseClick(Required<Button>(window, "OutputFullscreenExitButton"));
            window.WindowState = WindowState.Normal;

            RaiseClick(Required<Button>(window, "OutputFullscreenExitButton"));

            Assert.Equal(WindowState.Normal, window.WindowState);
            Assert.False(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);
        });

    [AvaloniaFact]
    public Task EnteringFullscreenTwicePreservesOriginalRestoreState() =>
        WithOutputPreviewAsync(window =>
        {
            window.WindowState = WindowState.Maximized;
            RaiseClick(Required<Button>(window, "OutputPreviewFullscreenButton"));
            RaiseClick(Required<Button>(window, "OutputPreviewFullscreenButton"));

            RaiseClick(Required<Button>(window, "OutputFullscreenExitButton"));

            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.False(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);
        });

    private static async Task WithOutputPreviewAsync(Action<MainWindow> action)
    {
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        try
        {
            window.Show();
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var fullscreen = Required<Button>(window, "OutputPreviewFullscreenButton");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, change) =>
            {
                if (change.Property == InputElement.IsEnabledProperty && fullscreen.IsEnabled)
                    ready.TrySetResult();
            };
            fullscreen.PropertyChanged += handler;
            try
            {
                Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
                if (fullscreen.IsEnabled) ready.TrySetResult();
                // This watchdog bounds a missing readiness signal; it is not a performance assertion.
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            finally
            {
                fullscreen.PropertyChanged -= handler;
            }

            action(window);
        }
        finally
        {
            window.Close();
        }
    }
}
