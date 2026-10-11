// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class UiFailureWindowTests
{
    [AvaloniaFact]
    public async Task FailureRestoresBusyControls()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            window.SetBusy(true, "Opening capture");

            await window.FailureReporter.Runner.RunAsync("capture.load", _ => Task.FromException(new InvalidOperationException("picker failed")));

            Assert.False(Required<ProgressBar>(window, "LoadingProgress").IsVisible);
            Assert.True(Required<Button>(window, "LoadButton").IsVisible);
            Assert.False(Required<Button>(window, "CancelButton").IsVisible);
            Assert.True(Required<TextBox>(window, "TargetI2cAddressTextBox").IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FailureUsesExistingLoadStatusPath()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            await window.FailureReporter.Runner.RunAsync("capture.load", _ => Task.FromException(new InvalidOperationException("picker failed")));

            Assert.Equal("Load failed · picker failed", Required<TextBlock>(window, "SessionStatusText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SuccessfulOperationPreservesStatus()
    {
        var window = new MainWindow();
        window.Show();
        try
        {
            var status = Required<TextBlock>(window, "SessionStatusText");
            status.Text = "Existing success status";

            await window.FailureReporter.Runner.RunAsync("capture.load", _ => Task.CompletedTask);

            Assert.Equal("Existing success status", status.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SharedApplicationRegistersGlobalHandlers()
    {
        var app = Assert.IsType<App>(Application.Current);

        Assert.True(app.ExceptionHandlersRegistered);
    }

    private static T Required<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException("Missing control: " + name);
}
