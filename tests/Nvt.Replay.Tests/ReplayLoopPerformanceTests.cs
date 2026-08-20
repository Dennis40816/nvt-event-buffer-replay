using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ReplayLoopPerformanceTests
{
    private const int TrailPointCount = 100_003;
    private const int ThreeMinuteFrameAdvances = 120 * 60 * 3;
    private const int LoopFrameCount = 128;
    private const int RandomSeekCount = 512;

    [Fact]
    public void Three_minute_loop_reaches_steady_state_without_rebuilding_history_or_accepting_stale_work()
    {
        var fixture = CreateFixture();
        var historyIdentity = fixture.Workspace.TrailHistory;
        var historyStatistics = historyIdentity.Statistics;
        var controller = Controller(fixture.Replay.Timeline);
        var randomIndices = RandomIndices(RandomSeekCount, TrailPointCount, seed: 0x51927);

        _ = MeasureLoop(controller, fixture.Workspace, 256);
        _ = MeasureRandomSeeks(fixture.Workspace, randomIndices[..16]);
        var firstLoop = MeasureLoop(controller, fixture.Workspace, ThreeMinuteFrameAdvances);
        var secondLoop = MeasureLoop(controller, fixture.Workspace, ThreeMinuteFrameAdvances);
        var firstRandom = MeasureRandomSeeks(fixture.Workspace, randomIndices);
        var secondRandom = MeasureRandomSeeks(fixture.Workspace, randomIndices);
        var staleGeneration = controller.Generation;
        var timingChange = controller.ConfigureTiming(
            ReplayPlaybackClockDomain.Frame,
            compressIdle: true,
            ReplayPlaybackRate.FromMultiplier(0.5));
        var indexBeforeStale = controller.CurrentIndex;
        var seeksBeforeStale = fixture.Replay.SeekCount;
        var firstStale = MeasureStaleAdvances(controller, staleGeneration, ThreeMinuteFrameAdvances);
        var secondStale = MeasureStaleAdvances(controller, staleGeneration, ThreeMinuteFrameAdvances);

        Assert.True(timingChange.RestartRequired);
        Assert.NotEqual(staleGeneration, timingChange.Generation);
        Assert.Equal(ThreeMinuteFrameAdvances, firstLoop.Advances);
        Assert.Equal(ThreeMinuteFrameAdvances, secondLoop.Advances);
        Assert.Equal(0, firstLoop.InvalidOperations + secondLoop.InvalidOperations);
        Assert.Same(historyIdentity, fixture.Workspace.TrailHistory);
        Assert.Equal(historyStatistics, fixture.Workspace.TrailHistory.Statistics);
        Assert.Equal(0, fixture.Workspace.Revision);
        Assert.Equal(0, fixture.Workspace.GeometryRevision);
        Assert.Same(firstLoop.FirstChunkSource, firstLoop.LastChunkSource);
        Assert.Same(firstLoop.FirstChunkSource, secondLoop.FirstChunkSource);
        Assert.Same(firstRandom.FirstChunkSource, secondRandom.FirstChunkSource);
        AssertSteadyAllocation(firstLoop.AllocatedBytes, secondLoop.AllocatedBytes);
        AssertSteadyAllocation(firstRandom.AllocatedBytes, secondRandom.AllocatedBytes);
        AssertSteadyAllocation(firstStale.AllocatedBytes, secondStale.AllocatedBytes);
        Assert.Equal(ThreeMinuteFrameAdvances, firstStale.StaleOperations);
        Assert.Equal(ThreeMinuteFrameAdvances, secondStale.StaleOperations);
        Assert.Equal(indexBeforeStale, controller.CurrentIndex);
        Assert.Equal(seeksBeforeStale, fixture.Replay.SeekCount);

        var report = new PerformanceReport(
            "1.0",
            Environment.MachineName,
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            Environment.GetEnvironmentVariable("NVT_REPLAY_PERF_COMMIT") ?? "unknown",
            new PerformanceInput(
                TrailPointCount,
                ThreeMinuteFrameAdvances,
                LoopFrameCount,
                RandomSeekCount,
                "fixed-seed:0x51927"),
            fixture.HistoryBuild,
            firstLoop,
            secondLoop,
            firstRandom,
            secondRandom,
            firstStale,
            secondStale,
            historyStatistics);
        Console.WriteLine(
            $"loop-performance history={fixture.HistoryBuild.ElapsedMilliseconds:N1}ms/{fixture.HistoryBuild.AllocatedBytes:N0}B " +
            $"loop1={firstLoop.ElapsedMilliseconds:N1}ms/{firstLoop.AllocatedBytes:N0}B " +
            $"loop2={secondLoop.ElapsedMilliseconds:N1}ms/{secondLoop.AllocatedBytes:N0}B " +
            $"random2={secondRandom.ElapsedMilliseconds:N1}ms/{secondRandom.AllocatedBytes:N0}B " +
            $"stale2={secondStale.ElapsedMilliseconds:N1}ms/{secondStale.AllocatedBytes:N0}B");
        WriteReportWhenRequested(report);
    }

    private static Fixture CreateFixture()
    {
        var snapshots = new GeneratedSnapshotList(TrailPointCount);
        var replay = new FakeReplay(snapshots);
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var history = ReplayTrailHistory.Create(snapshots);
        stopwatch.Stop();
        var historyBuild = new WorkMeasurement(
            GC.GetAllocatedBytesForCurrentThread() - before,
            stopwatch.Elapsed.TotalMilliseconds);
        var settings = ReplayPaintSettings.Default(new ReplayExtent(4096, 2048)) with
        {
            TrailRetention = ReplayTrailMode.Persistent,
            TrailLength = 120,
            LegendPosition = ReplayLegendPosition.TopLeft,
        };
        return new Fixture(
            replay,
            new ReplayPaintWorkspace(replay, history, settings, diagnostics: []),
            historyBuild);
    }

    private static ReplayPlaybackController Controller(IReadOnlyList<ReplayTimelineEntry> timeline)
    {
        var controller = new ReplayPlaybackController();
        controller.Load(timeline);
        controller.ConfigureTiming(
            ReplayPlaybackClockDomain.Frame,
            compressIdle: true,
            ReplayPlaybackRate.Normal);
        controller.ConfigureLoop(true, TrailPointCount - LoopFrameCount, TrailPointCount - 1);
        return controller;
    }

    private static LoopMeasurement MeasureLoop(
        ReplayPlaybackController controller,
        ReplayPaintWorkspace workspace,
        int advances)
    {
        controller.Pause();
        controller.Seek(TrailPointCount - LoopFrameCount);
        var start = controller.Play();
        if (start.Kind != ReplayPlaybackStartKind.Started)
            throw new InvalidOperationException($"Loop did not start: {start.Kind}");

        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var elapsed = TimeSpan.Zero;
        object? firstChunkSource = null;
        object? lastChunkSource = null;
        var invalidOperations = 0;
        long checksum = 0;
        for (var advance = 0; advance < advances; advance++)
        {
            elapsed += ReplayPlaybackController.MinimumVisualInterval;
            var operation = controller.Advance(start.Generation, elapsed);
            if (operation.Kind == ReplayPlaybackOperationKind.LoopRestart)
                elapsed = TimeSpan.Zero;
            else if (operation.Kind != ReplayPlaybackOperationKind.Move)
                invalidOperations++;

            var scene = workspace.CreateScene(operation.LogicalIndex);
            var trail = scene.ContactTrails.Count == 1
                ? scene.ContactTrails[0]
                : throw new InvalidOperationException("Persistent loop scene must contain one trail.");
            foreach (var chunk in ReplayTrailChunker.Enumerate(trail))
            {
                firstChunkSource ??= chunk.Source;
                lastChunkSource = chunk.Source;
                checksum = unchecked(checksum + chunk.Count + chunk.Offset);
            }
            checksum = unchecked(checksum + operation.LogicalIndex);
        }
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        return new LoopMeasurement(
            advances,
            invalidOperations,
            allocated,
            stopwatch.Elapsed.TotalMilliseconds,
            checksum,
            firstChunkSource,
            lastChunkSource);
    }

    private static SeekMeasurement MeasureRandomSeeks(
        ReplayPaintWorkspace workspace,
        IReadOnlyList<int> logicalIndices)
    {
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        object? firstChunkSource = null;
        object? lastChunkSource = null;
        long checksum = 0;
        for (var index = 0; index < logicalIndices.Count; index++)
        {
            var scene = workspace.CreateScene(logicalIndices[index]);
            if (scene.ContactTrails.Count == 0) continue;
            var first = ReplayTrailChunker.Enumerate(scene.ContactTrails[0]).First();
            firstChunkSource ??= first.Source;
            lastChunkSource = first.Source;
            checksum = unchecked(checksum + first.Count + scene.LogicalIndex);
        }
        stopwatch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(checksum);
        return new SeekMeasurement(
            logicalIndices.Count,
            allocated,
            stopwatch.Elapsed.TotalMilliseconds,
            checksum,
            firstChunkSource,
            lastChunkSource);
    }

    private static StaleMeasurement MeasureStaleAdvances(
        ReplayPlaybackController controller,
        int staleGeneration,
        int advances)
    {
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var staleOperations = 0;
        for (var advance = 0; advance < advances; advance++)
            if (controller.Advance(staleGeneration, ReplayPlaybackController.MinimumVisualInterval).Kind ==
                ReplayPlaybackOperationKind.Stale)
                staleOperations++;
        stopwatch.Stop();
        return new StaleMeasurement(
            staleOperations,
            GC.GetAllocatedBytesForCurrentThread() - before,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private static int[] RandomIndices(int count, int frameCount, int seed)
    {
        var random = new Random(seed);
        var result = new int[count];
        for (var index = 0; index < result.Length; index++)
            result[index] = random.Next(2, frameCount);
        return result;
    }

    private static void AssertSteadyAllocation(long firstRound, long secondRound)
    {
        var tolerance = Math.Max(256 * 1024L, firstRound / 10);
        Assert.InRange(secondRound, 0, firstRound + tolerance);
    }

    private static void WriteReportWhenRequested(PerformanceReport report)
    {
        var path = Environment.GetEnvironmentVariable("NVT_REPLAY_PERF_REPORT");
        if (string.IsNullOrWhiteSpace(path)) return;
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        }));
    }

    private sealed record Fixture(
        FakeReplay Replay,
        ReplayPaintWorkspace Workspace,
        WorkMeasurement HistoryBuild);

    private sealed record PerformanceReport(
        string SchemaVersion,
        string Machine,
        string OperatingSystem,
        string Framework,
        int ProcessorCount,
        string Commit,
        PerformanceInput Input,
        WorkMeasurement HistoryBuild,
        LoopMeasurement FirstLoop,
        LoopMeasurement SecondLoop,
        SeekMeasurement FirstRandomSeek,
        SeekMeasurement SecondRandomSeek,
        StaleMeasurement FirstStaleGeneration,
        StaleMeasurement SecondStaleGeneration,
        ReplayTrailStorageStatistics HistoryStatistics);

    private sealed record PerformanceInput(
        int TrailPoints,
        int LoopAdvances,
        int LoopFrames,
        int RandomSeeks,
        string RandomSequence);

    private sealed record WorkMeasurement(long AllocatedBytes, double ElapsedMilliseconds);

    private sealed record LoopMeasurement(
        int Advances,
        int InvalidOperations,
        long AllocatedBytes,
        double ElapsedMilliseconds,
        long Checksum,
        [property: JsonIgnore] object? FirstChunkSource,
        [property: JsonIgnore] object? LastChunkSource);

    private sealed record SeekMeasurement(
        int Seeks,
        long AllocatedBytes,
        double ElapsedMilliseconds,
        long Checksum,
        [property: JsonIgnore] object? FirstChunkSource,
        [property: JsonIgnore] object? LastChunkSource);

    private sealed record StaleMeasurement(
        int StaleOperations,
        long AllocatedBytes,
        double ElapsedMilliseconds);

    private sealed class FakeReplay(IReadOnlyList<ITouchReplaySnapshot> snapshots) : ITouchReplaySession
    {
        public int Count => snapshots.Count;
        public int SeekCount { get; private set; }
        public TimeSpan FrameInterval => TouchReplayOptions.NominalTpFrameInterval;
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; } =
            Enumerable.Range(0, snapshots.Count).Select(TimelineEntry).ToArray();
        public IReadOnlyList<ReplayDiagnostic> Diagnostics => [];
        public IReadOnlyList<ReplayContact> AllReportedContacts => [];

        public ITouchReplaySnapshot Seek(int logicalIndex)
        {
            SeekCount++;
            return snapshots[logicalIndex];
        }
    }

    private sealed class GeneratedSnapshotList(int count) : IReadOnlyList<ITouchReplaySnapshot>
    {
        private readonly ITouchReplaySnapshot[] snapshots =
            Enumerable.Range(0, count).Select(index => (ITouchReplaySnapshot)new GeneratedSnapshot(index)).ToArray();

        public int Count => snapshots.Length;
        public ITouchReplaySnapshot this[int index] => snapshots[index];
        public IEnumerator<ITouchReplaySnapshot> GetEnumerator() =>
            ((IEnumerable<ITouchReplaySnapshot>)snapshots).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => snapshots.GetEnumerator();
    }

    private sealed class GeneratedSnapshot : ITouchReplaySnapshot
    {
        private static readonly SourceRecord Source = new(
            0,
            "synthetic-100003-point-trail",
            DateTimeOffset.UnixEpoch,
            BusOperation.Paint,
            "TP",
            1,
            80,
            [],
            string.Empty,
            new SourceLocation(0, 1));
        private static readonly IReadOnlyList<SourceRecord> Sources = [Source];
        private readonly IReadOnlyList<ReplayContact> contacts;

        public GeneratedSnapshot(int logicalIndex)
        {
            LogicalIndex = logicalIndex;
            Timeline = TimelineEntry(logicalIndex);
            contacts =
            [
                new ReplayContact(
                    1,
                    TouchType.Finger,
                    logicalIndex == 0 ? TouchStatus.Enter : TouchStatus.Move,
                    (ushort)(logicalIndex % 4096),
                    (ushort)((logicalIndex * 7L) % 2048),
                    Source.StableId),
            ];
        }

        public int LogicalIndex { get; }
        public object DecodedFrame => this;
        public SourceRecord PrimarySource => Source;
        public IReadOnlyList<SourceRecord> PhysicalRecords => Sources;
        public ReplayTimelineEntry Timeline { get; }
        public IReadOnlyList<ReplayContact> ReportedContacts => contacts;
        public IReadOnlyList<ReplayContact> HostContacts => contacts;
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }

    private static ReplayTimelineEntry TimelineEntry(int index)
    {
        var time = TimeSpan.FromTicks(TouchReplayOptions.NominalTpFrameInterval.Ticks * index);
        return new ReplayTimelineEntry(
            index,
            time,
            time,
            time,
            index == 0 ? TimeSpan.Zero : TouchReplayOptions.NominalTpFrameInterval,
            false,
            false);
    }
}
