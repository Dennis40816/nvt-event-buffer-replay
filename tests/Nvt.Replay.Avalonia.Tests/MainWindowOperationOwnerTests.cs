// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowOperationOwnerTests
{
    [AvaloniaTheory]
    [InlineData("points", "Point plot export cancelled; no partial output was kept")]
    [InlineData("heatmap", "Heatmap export cancelled; no partial output was kept")]
    [InlineData("package", "Analysis cancelled; prior complete output was preserved")]
    public Task SupersededExportKeepsNewLoadBusyAndSilent(string content, string cancelledStatus) =>
        RunSupersededExportAsync(content, cancelledStatus, static (window, _) =>
        {
            Assert.True(Required<ProgressBar>(window, "LoadingProgress").IsVisible);
            Assert.True(Required<Button>(window, "CancelButton").IsVisible);
            Assert.False(Required<Button>(window, "LoadButton").IsVisible);
        });

    [AvaloniaTheory]
    [InlineData("points", "Point plot export cancelled; no partial output was kept")]
    [InlineData("heatmap", "Heatmap export cancelled; no partial output was kept")]
    [InlineData("package", "Analysis cancelled; prior complete output was preserved")]
    public Task SupersededExportKeepsNewLoadStatus(string content, string cancelledStatus) =>
        RunSupersededExportAsync(content, cancelledStatus, static (window, status) =>
            Assert.Equal(status, Required<TextBlock>(window, "SessionStatusText").Text));

    // The export's UI continuation is captured, so it runs on the test thread after the new load has begun
    // and before the new load can make progress. No operation is held by a blocking wait.
    private static async Task RunSupersededExportAsync(string content, string cancelledStatus,
        Action<MainWindow, string?> assertCurrentUi)
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        using var picker = new OperationExportPicker();
        using var locatorScope = picker.Install();
        var window = new MainWindow();
        window.Show();
        try
        {
            await window.OpenCaptureAsync(Fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var selector = Required<ComboBox>(window, "OutputContentComboBox");
            selector.SelectedItem = selector.Items.OfType<SelectOption>().First(option => option.Value == content);
            var continuation = new ExportContinuation();
            continuation.Start(() => Required<Button>(window, "ExportSelectedOutputButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Assert.True(Required<Button>(window, "CancelButton").IsVisible,
                "Export did not start: " + Required<TextBlock>(window, "SessionStatusText").Text);
            await continuation.PostedAsync(watchdog.Token);

            var newLoad = window.OpenCaptureAsync(Fixture);
            var newStatus = Required<TextBlock>(window, "SessionStatusText").Text;
            Assert.False(newLoad.IsCompleted);

            continuation.Complete();

            assertCurrentUi(window, newStatus);
            Assert.Equal(newStatus, Required<TextBlock>(window, "SessionStatusText").Text);
            Assert.NotEqual(cancelledStatus, Required<TextBlock>(window, "SessionStatusText").Text);
            await newLoad.WaitAsync(watchdog.Token);
            Assert.True(Required<Button>(window, "LoadButton").IsVisible);
            Assert.True(Required<Button>(window, "LoadButton").IsEnabled);
            Assert.False(Required<Button>(window, "CancelButton").IsVisible);
            Assert.False(Required<ProgressBar>(window, "LoadingProgress").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LateFailureCallbackKeepsNewLoadBusyAndStatus()
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow();
        window.Show();
        try
        {
            var oldOperation = window.FailureReporter.Runner.RunAsync("capture.load", _ => failure.Task.WaitAsync(watchdog.Token));
            var newLoad = window.OpenCaptureAsync(Fixture);
            var status = Required<TextBlock>(window, "SessionStatusText").Text;
            Assert.False(newLoad.IsCompleted);

            failure.SetException(new InvalidOperationException("superseded picker failure"));
            await oldOperation.WaitAsync(watchdog.Token);

            Assert.True(Required<ProgressBar>(window, "LoadingProgress").IsVisible);
            Assert.True(Required<Button>(window, "CancelButton").IsVisible);
            Assert.False(Required<Button>(window, "LoadButton").IsVisible);
            Assert.Equal(status, Required<TextBlock>(window, "SessionStatusText").Text);
            await newLoad.WaitAsync(watchdog.Token);
            Assert.False(Required<Button>(window, "CancelButton").IsVisible);
            Assert.True(Required<Button>(window, "LoadButton").IsEnabled);
        }
        finally
        {
            failure.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClosingWindowCancelsActiveLoadWithoutException()
    {
        using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        watchdog.CancelAfter(TimeSpan.FromSeconds(30));
        var window = new MainWindow();
        window.Show();
        try
        {
            var load = window.OpenCaptureAsync(Fixture);
            var status = Required<TextBlock>(window, "SessionStatusText").Text;
            Assert.False(load.IsCompleted);

            window.Close();
            await load.WaitAsync(watchdog.Token);

            Assert.False(window.IsVisible);
            Assert.Equal(0, Required<ListBox>(window, "RawRecordsList").ItemCount);
            Assert.Equal(status, Required<TextBlock>(window, "SessionStatusText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static string Fixture => System.IO.Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");

    private static T Required<T>(MainWindow window, string name) where T : Control =>
        window.FindControl<T>(name) ?? throw new InvalidOperationException("Missing control: " + name);

    // Capture only the export's UI continuation so its cancellation runs after the new load begins.
    private sealed class ExportContinuation : SynchronizationContext
    {
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private (SendOrPostCallback Callback, object? State)? _captured;

        public override void Post(SendOrPostCallback d, object? state)
        {
            if (_captured is not null) return;
            _captured = (d, state);
            _posted.TrySetResult();
        }

        internal void Start(Action action)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { action(); }
            finally { SetSynchronizationContext(previous); }
        }

        internal Task PostedAsync(CancellationToken token) => _posted.Task.WaitAsync(token);

        internal void Complete()
        {
            var continuation = _captured ?? throw new InvalidOperationException("No continuation was captured.");
            Start(() => continuation.Callback(continuation.State));
        }
    }
}
