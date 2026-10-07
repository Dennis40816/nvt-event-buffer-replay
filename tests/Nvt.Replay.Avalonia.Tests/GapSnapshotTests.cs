using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Rendering;
using Xunit;
using static Nvt.Replay.Avalonia.Tests.WorkspaceSnapshotTestHelpers;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class GapSnapshotTests
{
    [AvaloniaFact]
    public Task Raw_dark_keeps_both_rails_open_at_1180_pixels() =>
        CaptureRailsAsync(ThemeVariant.Dark, raw: true, "raw-rails-1180x720-dark.png");

    [AvaloniaFact]
    public Task Raw_light_keeps_both_rails_open_at_1180_pixels() =>
        CaptureRailsAsync(ThemeVariant.Light, raw: true, "raw-rails-1180x720-light.png");

    [AvaloniaFact]
    public Task Decoded_dark_keeps_both_rails_open_at_1180_pixels() =>
        CaptureRailsAsync(ThemeVariant.Dark, raw: false, "decoded-rails-1180x720-dark.png");

    [AvaloniaFact]
    public Task Decoded_light_keeps_both_rails_open_at_1180_pixels() =>
        CaptureRailsAsync(ThemeVariant.Light, raw: false, "decoded-rails-1180x720-light.png");

    [AvaloniaFact]
    public Task Output_loading_keeps_the_active_export_visible_at_1180_pixels() =>
        CaptureOutputExportAsync(requestCancel: false, "output-loading-1180x720-dark.png");

    [AvaloniaFact]
    public Task Output_cancel_request_keeps_the_active_export_visible_at_1180_pixels() =>
        CaptureOutputExportAsync(requestCancel: true, "output-cancel-1180x720-dark.png");

    [AvaloniaFact]
    public async Task Data_package_includes_PNG_at_1180_pixels()
    {
        var window = ShowWindow(1180, 720, ThemeVariant.Dark);
        try
        {
            await OpenDecodedFixtureAsync(window);
            await SelectOutputAsync(window);
            var outputContent = Required<ComboBox>(window, "OutputContentComboBox");
            outputContent.SelectedItem = outputContent.Items.OfType<SelectOption>()
                .Single(option => option.Value == "package");
            Stabilize(window);

            Assert.Equal("package", Assert.IsType<SelectOption>(outputContent.SelectedItem).Value);
            Assert.True(Required<Grid>(window, "OutputPackagePanel").IsVisible);
            // The package always includes its heatmap PNG; there is no separate PNG toggle.
            Assert.Contains("heatmap.png", Required<TextBlock>(window, "OutputFilesText").Text);
            Assert.Equal("ZIP-ready · 9 files", Required<TextBlock>(window, "OutputInfoFormatText").Text);
            Assert.Equal("Export package", Required<Button>(window, "ExportSelectedOutputButton").Content);

            VisualTestCapture.ProcessSnapshot(window, "package-png-1180x720-dark.png");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CaptureRailsAsync(ThemeVariant theme, bool raw, string snapshotName)
    {
        var window = ShowWindow(1180, 720, theme);
        try
        {
            await OpenDecodedFixtureAsync(window);
            var tabs = Required<TabControl>(window, "WorkspaceTabs");
            tabs.SelectedIndex = raw ? 0 : 1;
            var records = Required<ListBox>(window, raw ? "RawRecordsList" : "DecodedFramesList");
            if (raw)
            {
                records.SelectedItem = records.Items.OfType<RawRecordRow>()
                    .First(row => row.Record.Operation == Nvt.Replay.Core.BusOperation.Read);
                Required<TabControl>(window, "InspectorDetailsTabs").SelectedItem =
                    Required<TabItem>(window, "InspectorRawTab");
            }
            else
            {
                records.SelectedItem = records.Items.OfType<DecodedFrameRow>()
                    .First(row => row.Touches == 3);
            }
            ExpandReviewRail(window);
            ExpandInspectorRail(window);
            Stabilize(window);

            Assert.Equal(raw ? 0 : 1, tabs.SelectedIndex);
            Assert.NotNull(records.SelectedItem);
            Assert.True(records.Bounds.Width > 0);
            Assert.True(Required<Grid>(window, "ReviewRailContent").IsVisible);
            Assert.True(Required<Grid>(window, "InspectorRailContent").IsVisible);
            AssertInside(Required<Border>(window, "ReviewRailBorder"), window);
            AssertInside(Required<Border>(window, "InspectorRailBorder"), window);

            VisualTestCapture.ProcessSnapshot(window, snapshotName);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task CaptureOutputExportAsync(bool requestCancel, string snapshotName)
    {
        var window = ShowWindow(1180, 720, ThemeVariant.Dark);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExportJobHandle? handle = null;
        try
        {
            await OpenDecodedFixtureAsync(window);
            await SelectOutputAsync(window);
            handle = window.StartOutputExportJob(async (cancellationToken, progress) =>
            {
                // Determinate zero progress renders preparation without a moving highlight.
                progress.Report(new ReplayExportProgress(0, 120, "Preparing MP4 export"));
                // Hold the real Cancelling state until after its snapshot, even when cancelled.
                await release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The snapshot export must end through cancellation.");
            });
            await WaitUntilAsync(() =>
                Required<TextBlock>(window, "OutputExportProgressText").Text == "0% · 0/120");

            if (requestCancel)
                RaiseClick(Required<Button>(window, "OutputExportCancelButton"));
            Stabilize(window);

            Assert.False(handle.Completion.IsCompleted);
            Assert.True(Required<Border>(window, "OutputExportActivityPanel").IsVisible);
            Assert.Equal(
                requestCancel ? "Cancelling MP4 export…" : "Preparing MP4 export",
                Required<TextBlock>(window, "OutputExportStatusText").Text);
            Assert.Equal(!requestCancel, Required<Button>(window, "OutputExportCancelButton").IsEnabled);
            Assert.False(Required<Button>(window, "ExportSelectedOutputButton").IsVisible);
            Assert.False(Required<Button>(window, "LoadButton").IsEnabled);
            var progressBar = Required<ProgressBar>(window, "OutputExportProgressBar");
            Assert.False(progressBar.IsIndeterminate);
            Assert.Equal(0, progressBar.Value);
            AssertInside(
                Required<Border>(window, "OutputExportActivityPanel"),
                Required<Border>(window, "OutputInfoPanel"));

            VisualTestCapture.ProcessSnapshot(window, snapshotName);
        }
        finally
        {
            if (handle is not null)
            {
                window.CancelOutputExportJob();
                release.TrySetResult();
                await handle.Completion;
            }
            window.Close();
        }
    }

    private static async Task OpenDecodedFixtureAsync(MainWindow window)
    {
        await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
        await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
    }

    private static async Task SelectOutputAsync(MainWindow window)
    {
        Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
        await WaitUntilAsync(() => window.OutputPreviewPlanIdentity is not null);
    }
}
