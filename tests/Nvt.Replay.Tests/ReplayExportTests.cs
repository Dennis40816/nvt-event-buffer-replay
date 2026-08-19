using System.Security.Cryptography;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ReplayExportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-export-{Guid.NewGuid():N}");

    public ReplayExportTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Frame_plan_uses_selected_clock_speed_and_range_deterministically()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(100));
        var recorded = Options(Path.Combine(directory, "recorded.mp4"), frameRate: 30, speed: 1, ReplayExportClock.Recorded);
        var frame = recorded with { Clock = ReplayExportClock.Frame };

        Assert.Equal([2, 1], ReplayFramePlan.Build(replay, recorded).Select(item => item.RepeatCount));
        Assert.Equal([1], ReplayFramePlan.Build(replay, frame).Select(item => item.RepeatCount));
        Assert.Equal([0, 1], ReplayFramePlan.Build(replay, recorded).Select(item => item.LogicalIndex));
        Assert.Equal([0], ReplayFramePlan.Build(replay, frame).Select(item => item.LogicalIndex));
    }

    [Fact]
    public void Frame_plan_downsamples_120_hz_without_stretching_30_fps_video()
    {
        var replay = UniformReplay(120, TimeSpan.FromSeconds(1d / 120));
        var options = new ReplayExportOptions(
            Path.Combine(directory, "120hz.mp4"),
            new AnalysisRange(0, replay.Count - 1),
            320,
            180,
            30,
            1,
            ReplayExportClock.Frame);

        var plan = ReplayFramePlan.Build(replay, options);

        Assert.Equal(30, plan.Sum(item => item.RepeatCount));
        Assert.Equal(0, plan[0].LogicalIndex);
        Assert.Equal(119, plan[^1].LogicalIndex);
        Assert.All(plan, item => Assert.Equal(1, item.RepeatCount));
        Assert.Equal(0, ReplayFramePlan.LogicalIndexAt(plan, 0));
        Assert.Equal(119, ReplayFramePlan.LogicalIndexAt(plan, 29));
    }

    [Fact]
    public void Frame_plan_supports_custom_180_fps_and_rejects_rates_above_240()
    {
        var replay = UniformReplay(120, TimeSpan.FromSeconds(1d / 120));
        var options = new ReplayExportOptions(
            Path.Combine(directory, "180fps.mp4"),
            new AnalysisRange(0, replay.Count - 1),
            320,
            180,
            180,
            1,
            ReplayExportClock.Frame);

        Assert.Equal(180, ReplayFramePlan.OutputFrameCount(ReplayFramePlan.Build(replay, options)));
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReplayFramePlan.Build(replay, options with { FrameRate = 241 }));
        Assert.Contains("1-240", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_encoder_atomically_exports_a_headless_PNG_sequence_and_manifest()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var diagnostics = new[] { new ReplayDiagnostic(DiagnosticSeverity.Alarm, "COMMON_ASIL_ALARM", "alarm", "source-0", new SourceLocation(0, 1)) };
        var markers = new[] { new ReplayMarker("m1", "QA point", 0, 0, DateTimeOffset.UnixEpoch) };
        var output = Path.Combine(directory, "replay.mp4");

        var result = await new ReplayRangeExporter().ExportAsync(
            replay,
            index => ReplaySceneFactory.Create(replay.Seek(index), replay.Count, extent, diagnostics, markers),
            Options(output, ffmpeg: Path.Combine(directory, "missing-ffmpeg.exe")));

        Assert.Equal(ReplayExportKind.PngSequence, result.Kind);
        Assert.False(File.Exists(output));
        Assert.True(Directory.Exists(output + ".frames"));
        Assert.Single(Directory.GetFiles(output + ".frames", "frame-*.png"));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Contains("No reviewed FFmpeg", result.Warning);
        Assert.Contains("pngSequence", await File.ReadAllTextAsync(result.ManifestPath));
        var firstFrame = Directory.GetFiles(output + ".frames", "frame-*.png").Order().First();
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], (await File.ReadAllBytesAsync(firstFrame))[..8]);
        var hash = Hash(firstFrame);
        Assert.Equal("f6306b56e788d686904c3fc8a0068939f7d64f9e5df632d65db0d79250deaf27", hash);
        Assert.Empty(Directory.GetDirectories(directory, ".*.tmp"));
    }

    [Fact]
    public async Task Export_reports_frame_progress_through_fallback_completion()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var updates = new List<ReplayExportProgress>();
        var output = Path.Combine(directory, "progress.mp4");

        var result = await new ReplayRangeExporter().ExportAsync(
            replay,
            index => ReplaySceneFactory.Create(replay.Seek(index), replay.Count, extent),
            Options(output, ffmpeg: Path.Combine(directory, "missing-ffmpeg.exe")),
            progress: new InlineProgress<ReplayExportProgress>(updates.Add));

        Assert.Equal(ReplayExportKind.PngSequence, result.Kind);
        Assert.NotEmpty(updates);
        Assert.Equal("Preparing encoder", updates[0].Stage);
        Assert.Contains(updates, item => item.Stage == "Writing PNG fallback");
        Assert.Equal(updates[^1].TotalFrames, updates[^1].CompletedFrames);
        Assert.Equal(100, updates[^1].Percent);
    }

    [Fact]
    public void Render_settings_apply_theme_grid_and_legend_to_the_same_export_renderer()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var scene = ReplaySceneFactory.Create(replay.Seek(0), replay.Count, extent);
        var dark = new ReplayRenderSettings(ReplayRenderMode.Compare);
        var light = dark with
        {
            Theme = ReplayRenderTheme.Light,
            StrongGrid = true,
            LegendCollapsed = false,
            LegendPosition = ReplayLegendPosition.BottomRight,
        };

        var darkFrame = ReplayFrameRenderer.RenderRgb(scene, 320, 180, dark);
        var lightFrame = ReplayFrameRenderer.RenderRgb(scene, 320, 180, light);

        Assert.Equal([16, 23, 26], darkFrame[..3]);
        Assert.Equal([237, 242, 239], lightFrame[..3]);
        Assert.NotEqual(SHA256.HashData(darkFrame), SHA256.HashData(lightFrame));
    }

    [Fact]
    public void Trail_line_and_every_reported_point_are_independent_render_layers()
    {
        var replay = UniformReplay(20, TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var trail = new ReplayContactTrail(
            1,
            TouchType.Finger,
            [
                new ReplayTrailPoint(600, 300, TouchStatus.Enter, 0),
                new ReplayTrailPoint(850, 520, TouchStatus.Move, 6),
                new ReplayTrailPoint(1100, 260, TouchStatus.Move, 12),
                new ReplayTrailPoint(1350, 520, TouchStatus.Move, 19),
            ]);
        var scene = ReplaySceneFactory.Create(
            replay.Seek(replay.Count - 1),
            replay.Count,
            extent,
            contactTrails: [trail]);
        var both = new ReplayRenderSettings(ReplayRenderMode.HostState);
        var lineOnly = both with { ShowTrailPoints = false };
        var pointsOnly = both with { ShowTrailLines = false };
        var neither = both with { ShowTrailLines = false, ShowTrailPoints = false };

        var hashes = new[] { both, lineOnly, pointsOnly, neither }
            .Select(settings => Convert.ToHexString(SHA256.HashData(
                ReplayFrameRenderer.RenderRgb(scene, 640, 360, settings))))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(4, hashes.Count);
        Assert.Equal(4, Assert.Single(scene.ContactTrails).Points.Count);
    }

    [Fact]
    public void Reusable_render_buffer_is_pixel_identical_to_allocating_renderer()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var scene = ReplaySceneFactory.Create(replay.Seek(0), replay.Count, extent);
        var settings = new ReplayRenderSettings(
            ReplayRenderMode.Compare,
            ReplayRenderTheme.Light,
            StrongGrid: true,
            LegendVisible: true,
            LegendCollapsed: false,
            ReplayLegendPosition.TopLeft);
        var expected = ReplayFrameRenderer.RenderRgb(scene, 320, 180, settings);
        var actual = new byte[320 * 180 * 3];

        ReplayFrameRenderer.RenderRgb(scene, 320, 180, settings, actual);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Reusable_render_buffer_rejects_incorrect_size()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var scene = ReplaySceneFactory.Create(replay.Seek(0), replay.Count, extent);

        var exception = Assert.Throws<ArgumentException>(() => ReplayFrameRenderer.RenderRgb(
            scene,
            320,
            180,
            new ReplayRenderSettings(ReplayRenderMode.Compare),
            new byte[(320 * 180 * 3) - 1]));

        Assert.Equal("destination", exception.ParamName);
    }

    [Fact]
    public async Task Export_manifest_records_the_visual_settings_used_by_preview_and_mp4()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var output = Path.Combine(directory, "styled.mp4");
        var settings = new ReplayRenderSettings(
            ReplayRenderMode.HostState,
            ReplayRenderTheme.Light,
            StrongGrid: true,
            LegendVisible: false,
            LegendCollapsed: true,
            ReplayLegendPosition.BottomRight);
        var options = Options(output, ffmpeg: Path.Combine(directory, "missing-ffmpeg.exe")) with
        {
            Mode = ReplayRenderMode.HostState,
            RenderSettings = settings,
        };

        var result = await new ReplayRangeExporter().ExportAsync(
            replay,
            index => ReplaySceneFactory.Create(replay.Seek(index), replay.Count, extent),
            options);
        var manifest = await File.ReadAllTextAsync(result.ManifestPath);

        Assert.Contains("\"schemaVersion\": 2", manifest);
        Assert.Contains("\"theme\": \"light\"", manifest);
        Assert.Contains("\"strongGrid\": true", manifest);
        Assert.Contains("\"legendVisible\": false", manifest);
        Assert.Contains("\"legendPosition\": \"bottomRight\"", manifest);
        Assert.Contains("\"showTrailLines\": true", manifest);
        Assert.Contains("\"showTrailPoints\": true", manifest);
    }

    [Fact]
    public async Task Existing_output_is_never_overwritten()
    {
        var output = Path.Combine(directory, "existing.mp4");
        await File.WriteAllTextAsync(output, "prior-video");
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);

        await Assert.ThrowsAsync<IOException>(() => new ReplayRangeExporter().ExportAsync(
            replay,
            index => ReplaySceneFactory.Create(replay.Seek(index), replay.Count, extent),
            Options(output)));

        Assert.Equal("prior-video", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public void Axis_reversal_changes_rendering_without_changing_contact_coordinates()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var snapshot = replay.Seek(0);
        var normal = ReplaySceneFactory.Create(snapshot, replay.Count, extent);
        var reversed = ReplaySceneFactory.Create(snapshot, replay.Count, extent, reverseX: true, reverseY: true);

        Assert.Equal(normal.ReportedContacts, reversed.ReportedContacts);
        Assert.True(reversed.ReverseX);
        Assert.True(reversed.ReverseY);
        Assert.NotEqual(
            SHA256.HashData(ReplayFrameRenderer.RenderRgb(normal, 320, 180)),
            SHA256.HashData(ReplayFrameRenderer.RenderRgb(reversed, 320, 180)));
    }

    [Fact]
    public void Axis_swap_transposes_the_view_without_changing_source_coordinates()
    {
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        var snapshot = replay.Seek(0);
        var normal = ReplaySceneFactory.Create(snapshot, replay.Count, extent);
        var swapped = ReplaySceneFactory.Create(snapshot, replay.Count, extent, swapAxes: true);
        var contact = Assert.Single(snapshot.ReportedContacts);
        var viewCoordinates = swapped.ViewCoordinates(contact.X, contact.Y);

        Assert.True(swapped.SwapAxes);
        Assert.Equal(normal.Extent, swapped.Extent);
        Assert.Equal(extent.MaximumY, swapped.ViewExtent.MaximumX);
        Assert.Equal(extent.MaximumX, swapped.ViewExtent.MaximumY);
        Assert.Equal(contact.Y, viewCoordinates.X);
        Assert.Equal(contact.X, viewCoordinates.Y);
        Assert.Equal(contact.X, Assert.Single(swapped.ReportedContacts).X);
        Assert.Equal(contact.Y, Assert.Single(swapped.ReportedContacts).Y);
        Assert.NotEqual(
            SHA256.HashData(ReplayFrameRenderer.RenderRgb(normal, 320, 180)),
            SHA256.HashData(ReplayFrameRenderer.RenderRgb(swapped, 320, 180)));
    }

    [Fact]
    public async Task Cancellation_removes_fallback_staging_directory()
    {
        var output = Path.Combine(directory, "cancelled.mp4");
        var replay = Replay(TimeSpan.FromMilliseconds(10));
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ReplayRangeExporter().ExportAsync(
            replay,
            index => ReplaySceneFactory.Create(replay.Seek(index), replay.Count, extent),
            Options(output),
            cancellation.Token));

        Assert.False(Directory.Exists(output + ".frames"));
        Assert.Empty(Directory.GetDirectories(directory, ".*.tmp"));
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static ReplayExportOptions Options(
        string path,
        int frameRate = 30,
        double speed = 1,
        ReplayExportClock clock = ReplayExportClock.Recorded,
        string? ffmpeg = null) =>
        new(path, new AnalysisRange(0, 1), 320, 180, frameRate, speed, clock, ReplayRenderMode.Compare, ffmpeg,
            "capture.txt", new string('a', 64), new ReplayDecodeConfiguration("0x83", null, "synthetic"));

    private static FakeReplay Replay(TimeSpan secondTime)
    {
        var source0 = Source(0, "source-0", TimeSpan.Zero);
        var source1 = Source(1, "source-1", secondTime);
        return new FakeReplay(
        [
            Snapshot(0, source0, TimeSpan.Zero, new ReplayContact(1, TouchType.Finger, TouchStatus.Enter, 600, 300, source0.StableId)),
            Snapshot(1, source1, secondTime, new ReplayContact(1, TouchType.Finger, TouchStatus.Move, 900, 500, source1.StableId)),
        ], TimeSpan.FromMilliseconds(10));
    }

    private static FakeReplay UniformReplay(int frameCount, TimeSpan interval)
    {
        var snapshots = Enumerable.Range(0, frameCount)
            .Select(index =>
            {
                var source = Source(index, $"source-{index}", interval * index);
                return Snapshot(
                    index,
                    source,
                    interval * index,
                    new ReplayContact(1, TouchType.Finger, index == 0 ? TouchStatus.Enter : TouchStatus.Move,
                        (ushort)(600 + index), 300, source.StableId));
            })
            .ToArray();
        return new FakeReplay(snapshots, interval);
    }

    private static SourceRecord Source(int index, string id, TimeSpan time) =>
        new(index, id, DateTimeOffset.UnixEpoch + time, BusOperation.Paint, "TP", 1, 80, [], "", new SourceLocation(index * 10, index + 1));

    private static ITouchReplaySnapshot Snapshot(int index, SourceRecord source, TimeSpan time, ReplayContact contact) =>
        new FakeSnapshot(index, source, new ReplayTimelineEntry(index, time, TimeSpan.FromMilliseconds(index * 10), time, index == 0 ? TimeSpan.Zero : time, false, false), [contact]);

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed record FakeSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        ReplayTimelineEntry Timeline,
        IReadOnlyList<ReplayContact> ReportedContacts) : ITouchReplaySnapshot
    {
        public object DecodedFrame => this;
        public IReadOnlyList<SourceRecord> PhysicalRecords => [PrimarySource];
        public IReadOnlyList<ReplayContact> HostContacts => ReportedContacts;
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }

    private sealed class FakeReplay(IReadOnlyList<ITouchReplaySnapshot> snapshots, TimeSpan interval) : ITouchReplaySession
    {
        public int Count => snapshots.Count;
        public TimeSpan FrameInterval => interval;
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; } = snapshots.Select(item => item.Timeline).ToArray();
        public IReadOnlyList<ReplayDiagnostic> Diagnostics => [];
        public IReadOnlyList<ReplayContact> AllReportedContacts { get; } = snapshots.SelectMany(item => item.ReportedContacts).ToArray();
        public ITouchReplaySnapshot Seek(int logicalIndex) => snapshots[logicalIndex];
    }
}
