using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class ExportProgressBaselineCaptureTests
{
    [AvaloniaFact]
    public async Task Capture_export_progress_states_when_opted_in()
    {
        var directory = Environment.GetEnvironmentVariable("NVT_PROGRESS_BASELINE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        Directory.CreateDirectory(directory);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        var previousAuditDirectory = Environment.GetEnvironmentVariable("NVT_UI_AUDIT_DIR");
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        // CapturePng is private; CaptureHash saves the same bytes through its audit-directory option.
        Environment.SetEnvironmentVariable("NVT_UI_AUDIT_DIR", directory);
        var window = ShowWindow();
        var state = new StringBuilder();
        var knownRelease = Gate();
        var knownReported = Gate();
        var unknownRelease = Gate();
        var unknownReported = Gate();
        var cancellation = Gate();
        var finish = Gate();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => window.OutputPreviewPlanIdentity is not null);

            var handle = window.StartOutputExportJob(async (token, progress) =>
            {
                using var registration = token.Register(() => cancellation.TrySetResult());
                await knownRelease.Task;
                progress.Report(new ReplayExportProgress(12, 100, "Encoding MP4"));
                knownReported.SetResult();
                await unknownRelease.Task;
                progress.Report(new ReplayExportProgress(12, 0, "Encoding MP4"));
                unknownReported.SetResult();
                await cancellation.Task;
                await finish.Task;
                token.ThrowIfCancellationRequested();
                return Result();
            });
            knownRelease.SetResult();
            await knownReported.Task;
            window.PresentOutputExportSnapshot(new ExportJobSnapshot(handle.JobId, ExportJobStatus.Running));
            Capture("running-known");

            unknownRelease.SetResult();
            await unknownReported.Task;
            window.PresentOutputExportSnapshot(new ExportJobSnapshot(handle.JobId, ExportJobStatus.Running));
            Capture("running-unknown");

            Assert.True(window.CancelOutputExportJob());
            Capture("cancelling");
            finish.SetResult();
            var completed = await handle.Completion;
            Assert.Equal(ExportJobStatus.Cancelled, completed.Status);
            window.PresentOutputExportSnapshot(completed);
            Capture("cancelled");

            foreach (var terminalState in new[] { "succeeded", "failed" })
            {
                var release = Gate();
                var reported = Gate();
                var complete = Gate();
                var terminal = window.StartOutputExportJob(async (_, progress) =>
                {
                    await release.Task;
                    progress.Report(new ReplayExportProgress(100, 100, "Encoding MP4"));
                    reported.SetResult();
                    await complete.Task;
                    if (terminalState == "failed") throw new InvalidOperationException("baseline failure");
                    return Result();
                });
                release.SetResult();
                await reported.Task;
                window.PresentOutputExportSnapshot(new ExportJobSnapshot(terminal.JobId, ExportJobStatus.Running));
                complete.SetResult();
                var result = await terminal.Completion;
                Assert.Equal(terminalState == "failed" ? ExportJobStatus.Failed : ExportJobStatus.Succeeded, result.Status);
                window.PresentOutputExportSnapshot(result);
                Capture(terminalState);
            }

            await File.WriteAllTextAsync(Path.Combine(directory, "export-progress-ui-state.txt"),
                state.ToString(), new UTF8Encoding(false));
        }
        finally
        {
            // Release fake operations even if a capture/assertion fails before normal completion.
            window.CancelOutputExportJob();
            knownRelease.TrySetResult();
            unknownRelease.TrySetResult();
            finish.TrySetResult();
            window.Close();
            Environment.SetEnvironmentVariable("NVT_UI_AUDIT_DIR", previousAuditDirectory);
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        void Capture(string name)
        {
            VisualTestCapture.Stabilize(window);
            VisualTestCapture.CaptureHash(window, $"export-{name}.png");
            state.Append('[').Append(name).Append("]\n");
            Write("OutputExportActivityPanel.IsVisible", Required<Border>(window, "OutputExportActivityPanel").IsVisible);
            Write("ExportSelectedOutputButton.IsVisible", Required<Button>(window, "ExportSelectedOutputButton").IsVisible);
            Write("ExportSelectedOutputButton.IsEnabled", Required<Button>(window, "ExportSelectedOutputButton").IsEnabled);
            Write("OutputExportCancelButton.IsEnabled", Required<Button>(window, "OutputExportCancelButton").IsEnabled);
            Write("LoadButton.IsEnabled", Required<Button>(window, "LoadButton").IsEnabled);
            Write("OutputExportProgressBar.IsIndeterminate", Required<ProgressBar>(window, "OutputExportProgressBar").IsIndeterminate);
            Write("OutputExportProgressBar.Value", Required<ProgressBar>(window, "OutputExportProgressBar").Value);
            Write("OutputExportStatusText.Text", Required<TextBlock>(window, "OutputExportStatusText").Text);
            Write("OutputExportProgressText.Text", Required<TextBlock>(window, "OutputExportProgressText").Text);
            Write("SessionStatusText.Text", Required<TextBlock>(window, "SessionStatusText").Text);
            state.Append('\n');
        }

        void Write(string name, object? value)
        {
            var text = value switch
            {
                null => "-",
                IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString()!
            };
            state.Append(name).Append('=').Append(text.Replace("\\", "\\\\")
                .Replace("\r", "\\r").Replace("\n", "\\n")).Append('\n');
        }
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ReplayExportResult Result() => new(
        ReplayExportKind.Mp4, "baseline.mp4", "baseline.manifest.json", 100, TimeSpan.FromSeconds(1), null);

    private static MainWindow ShowWindow()
    {
        if (Application.Current is { } application) application.RequestedThemeVariant = ThemeVariant.Dark;
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        window.Show();
        VisualTestCapture.Stabilize(window);
        return window;
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control '{name}'.");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Timed out while waiting for the output preview.");
    }
}
