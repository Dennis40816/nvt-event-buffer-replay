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
        Assert.Equal("439f9bcbf97760c7f2cc0fc37bf3f0de1c3a84d2375a3b9ca93b89b5f0f338c1", hash);
        Assert.Empty(Directory.GetDirectories(directory, ".*.tmp"));
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
