// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using static Nvt.Replay.Avalonia.Tests.WorkspaceSnapshotTestHelpers;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowFullscreenEscapeTests
{
    [AvaloniaFact]
    public async Task EscapeLeavesFullscreenAndRestoresPreviousWindowState()
    {
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        try
        {
            window.Show();
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var fullscreen = Required<Button>(window, "OutputPreviewFullscreenButton");
            Assert.False(fullscreen.IsEnabled);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, change) => SignalEnabled(fullscreen, change, ready);
            fullscreen.PropertyChanged += handler;
            try
            {
                Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
                // This watchdog bounds a missing readiness signal; it is not a performance assertion.
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
            finally
            {
                fullscreen.PropertyChanged -= handler;
            }
            window.WindowState = WindowState.Maximized;
            RaiseClick(fullscreen);
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            Assert.True(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);

            Assert.Equal(WindowState.Maximized, window.WindowState);
            Assert.False(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static void SignalEnabled(Button button, AvaloniaPropertyChangedEventArgs change, TaskCompletionSource ready)
    {
        if (change.Property == InputElement.IsEnabledProperty && button.IsEnabled)
            ready.TrySetResult();
    }
}
