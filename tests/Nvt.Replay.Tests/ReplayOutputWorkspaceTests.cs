using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ReplayOutputWorkspaceTests
{
    [Fact]
    public void Defaults_match_the_frame_paced_120_Hz_output_contract()
    {
        var workspace = new ReplayOutputWorkspace();

        Assert.Equal(ReplayOutputContentType.Mp4Video, workspace.Settings.ContentType);
        Assert.Equal(ReplayExportClock.Frame, workspace.Settings.Clock);
        Assert.Equal(120, workspace.Settings.FrameRate);
        Assert.Equal(1, workspace.Settings.Speed);
        Assert.Equal((1920, 1080), (workspace.Settings.Width, workspace.Settings.Height));
    }

    [Fact]
    public void Equal_updates_do_not_invalidate_the_workspace()
    {
        var workspace = new ReplayOutputWorkspace();

        Assert.False(workspace.Update(workspace.Settings with { }));
        Assert.Equal(0, workspace.Revision);
        Assert.True(workspace.Update(workspace.Settings with { Speed = 0.5 }));
        Assert.Equal(1, workspace.Revision);
    }

    [Fact]
    public void Validation_is_content_specific_and_keeps_video_limits_canonical()
    {
        var replay = new FakeReplay(10);
        var invalidVideo = ReplayOutputSettings.Default with
        {
            Speed = double.NaN,
            FrameRate = ReplayFramePlan.MaximumFrameRate + 1,
            Width = 319,
            Height = 179,
            Range = new AnalysisRange(-1, 20),
        };
        var workspace = new ReplayOutputWorkspace(invalidVideo);

        var video = workspace.Validate(replay);

        Assert.False(video.IsValid);
        Assert.Equal(4, video.Errors.Count);
        Assert.Contains(video.Errors, error => error.Contains("range", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(video.Errors, error => error.Contains("speed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(video.Errors, error => error.Contains("frame rate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(video.Errors, error => error.Contains("size", StringComparison.OrdinalIgnoreCase));

        workspace.Update(invalidVideo with
        {
            ContentType = ReplayOutputContentType.Heatmap,
            Range = new AnalysisRange(0, 9),
        });
        Assert.True(workspace.Validate(replay).IsValid);
    }

    [Fact]
    public void Preview_uses_the_canonical_frame_plan_and_has_an_immutable_identity()
    {
        var replay = new FakeReplay(120);
        var settings = ReplayOutputSettings.Default with
        {
            FrameRate = 30,
            Width = 1280,
            Height = 720,
            Range = new AnalysisRange(0, 119),
        };
        var workspace = new ReplayOutputWorkspace(settings);

        var first = workspace.BuildVideoPreview(replay, "capture-sha/0x83");
        var second = workspace.BuildVideoPreview(replay, "capture-sha/0x83");

        Assert.Equal(30, first.Estimate.OutputFrameCount);
        Assert.Equal(TimeSpan.FromSeconds(1), first.Estimate.Duration);
        Assert.Equal(first.Identity, second.Identity);
        Assert.Same(first.Entries, first.FramePlan);
        Assert.Same(
            first.FramePlan,
            ReplayFramePlan.Freeze(replay, workspace.CreateExportOptions("same-plan.mp4"), first.Entries));
        Assert.Equal(
            ReplayFramePlan.Build(replay, workspace.CreateExportOptions("elsewhere.mp4")),
            first.Entries);

        workspace.Update(settings with { Width = 1920 });
        var changed = workspace.BuildVideoPreview(replay, "capture-sha/0x83");
        Assert.NotEqual(first.Identity, changed.Identity);
        Assert.Equal(first.Estimate.OutputFrameCount, changed.Estimate.OutputFrameCount);
    }

    [Fact]
    public void Export_options_preserve_the_existing_public_export_contract()
    {
        var range = new AnalysisRange(2, 8);
        var settings = ReplayOutputSettings.Default with
        {
            Clock = ReplayExportClock.Recorded,
            Speed = 1.75,
            FrameRate = 180,
            Width = 2560,
            Height = 1280,
            Range = range,
        };
        var workspace = new ReplayOutputWorkspace(settings);
        var render = new ReplayRenderSettings(ReplayRenderMode.HostState);

        var options = workspace.CreateExportOptions(
            "out.mp4",
            ReplayRenderMode.HostState,
            "ffmpeg.exe",
            "capture.csv",
            "abc",
            renderSettings: render);

        Assert.Equal("out.mp4", options.OutputPath);
        Assert.Equal(range, options.Range);
        Assert.Equal((2560, 1280, 180, 1.75), (options.Width, options.Height, options.FrameRate, options.Speed));
        Assert.Equal(ReplayExportClock.Recorded, options.Clock);
        Assert.Equal(ReplayRenderMode.HostState, options.Mode);
        Assert.Equal("ffmpeg.exe", options.FfmpegPath);
        Assert.Equal("capture.csv", options.SourceFileName);
        Assert.Equal("abc", options.SourceSha256);
        Assert.Equal(render, options.RenderSettings);
    }

    [Fact]
    public void Estimate_flags_long_exports_and_saturates_large_byte_work()
    {
        var replay = new FakeReplay(10_000);
        var settings = ReplayOutputSettings.Default with
        {
            Width = ReplayOutputWorkspace.MaximumWidth,
            Height = ReplayOutputWorkspace.MaximumHeight,
            Range = new AnalysisRange(0, 9_999),
        };
        var preview = new ReplayOutputWorkspace(settings).BuildVideoPreview(replay, "long");

        Assert.Equal(10_000, preview.Estimate.OutputFrameCount);
        Assert.True(preview.Estimate.RequiresConfirmation);
        Assert.True(preview.Estimate.PixelFrameWork > 0);
        Assert.True(preview.Estimate.EstimatedRgbaBytes > preview.Estimate.PixelFrameWork);
    }

    [Fact]
    public void Preview_forwards_planning_progress_and_cancellation()
    {
        var replay = new FakeReplay(20_000);
        var workspace = new ReplayOutputWorkspace(ReplayOutputSettings.Default with
        {
            Range = new AnalysisRange(0, 19_999),
        });
        using var cancellation = new CancellationTokenSource();
        var updates = new List<ReplayExportProgress>();
        var progress = new InlineProgress<ReplayExportProgress>(item =>
        {
            updates.Add(item);
            if (item.CompletedFrames > 0) cancellation.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() =>
            workspace.BuildVideoPreview(replay, "cancel", cancellation.Token, progress));
        Assert.NotEmpty(updates);
        Assert.Equal("Planning duration", updates[0].Stage);
    }

    [Fact]
    public async Task Plan_service_is_non_blocking_single_flight_and_reuses_sampling_for_resolution_changes()
    {
        var replay = new FakeReplay(120);
        var settings = ReplayOutputSettings.Default with
        {
            FrameRate = 30,
            Range = new AnalysisRange(0, 119),
        };
        using var plannerStarted = new ManualResetEventSlim();
        using var releasePlanner = new ManualResetEventSlim();
        using var service = new ReplayOutputPlanService((candidate, requested, cancellationToken, progress) =>
        {
            plannerStarted.Set();
            releasePlanner.Wait(cancellationToken);
            return ReplayFramePlan.BuildSnapshot(
                candidate,
                new ReplayOutputWorkspace(requested).CreateExportOptions("preview.mp4"),
                cancellationToken,
                progress);
        });

        var first = service.GetPreviewAsync(replay, "capture/config", settings);
        Assert.True(plannerStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(first.IsCompleted);
        var resized = service.GetPreviewAsync(
            replay,
            "capture/config",
            settings with { Width = 2560, Height = 1280 });
        Assert.False(resized.IsCompleted);
        Assert.Equal(1, service.BuildCount);

        releasePlanner.Set();
        var firstPreview = await first;
        var resizedPreview = await resized;

        Assert.Same(firstPreview.FramePlan, resizedPreview.FramePlan);
        Assert.Equal((1920, 1080), (firstPreview.Identity.Settings.Width, firstPreview.Identity.Settings.Height));
        Assert.Equal((2560, 1280), (resizedPreview.Identity.Settings.Width, resizedPreview.Identity.Settings.Height));
        Assert.Equal(firstPreview.Identity.SamplingIdentity, resizedPreview.Identity.SamplingIdentity);
    }

    [Fact]
    public async Task Plan_service_surfaces_the_recorded_gap_hard_limit_without_retrying_same_identity()
    {
        var replay = new FakeReplay(2, recordedInterval: TimeSpan.FromDays(10));
        var settings = ReplayOutputSettings.Default with
        {
            Clock = ReplayExportClock.Recorded,
            FrameRate = ReplayFramePlan.MaximumFrameRate,
            Range = new AnalysisRange(0, 1),
        };
        using var service = new ReplayOutputPlanService();

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetPreviewAsync(replay, "large-gap/config", settings));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetPreviewAsync(replay, "large-gap/config", settings));

        Assert.Contains("safety limit", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, service.BuildCount);
    }

    private sealed class FakeReplay : ITouchReplaySession
    {
        public FakeReplay(int count, TimeSpan? frameInterval = null, TimeSpan? recordedInterval = null)
        {
            Count = count;
            FrameInterval = frameInterval ?? TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 120);
            var recorded = recordedInterval ?? FrameInterval;
            Timeline = Enumerable.Range(0, count)
                .Select(index => new ReplayTimelineEntry(
                    index,
                    TimeSpan.FromTicks(recorded.Ticks * index),
                    TimeSpan.FromTicks(FrameInterval.Ticks * index),
                    TimeSpan.FromTicks(FrameInterval.Ticks * index),
                    index == 0 ? TimeSpan.Zero : recorded,
                    false,
                    false))
                .ToArray();
        }

        public int Count { get; }
        public TimeSpan FrameInterval { get; }
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; }
        public IReadOnlyList<ReplayDiagnostic> Diagnostics => [];
        public IReadOnlyList<ReplayContact> AllReportedContacts => [];
        public ITouchReplaySnapshot Seek(int logicalIndex) => throw new NotSupportedException();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
