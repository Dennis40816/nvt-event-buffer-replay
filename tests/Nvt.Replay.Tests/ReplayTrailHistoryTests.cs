using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ReplayTrailHistoryTests
{
    [Fact]
    public void Until_break_keeps_the_active_gesture_and_clears_it_on_break()
    {
        var history = ReplayTrailHistory.Create(Replay());

        var active = Assert.Single(history.Build(1, ReplayTrailMode.UntilBreak));
        Assert.Equal([0, 1], active.Points.Select(point => point.LogicalIndex));
        Assert.Empty(history.Build(2, ReplayTrailMode.UntilBreak));
    }

    [Fact]
    public void Persistent_keeps_completed_gestures_without_joining_reused_ids()
    {
        var trails = ReplayTrailHistory.Create(Replay()).Build(4, ReplayTrailMode.Persistent);

        Assert.Equal(2, trails.Count);
        Assert.Equal([0, 1, 2], trails[0].Points.Select(point => point.LogicalIndex));
        Assert.Equal([3, 4], trails[1].Points.Select(point => point.LogicalIndex));
    }

    [Fact]
    public void Recent_and_clear_boundary_limit_visible_evidence_only()
    {
        var history = ReplayTrailHistory.Create(Replay());

        var recent = Assert.Single(history.Build(4, ReplayTrailMode.Recent, recentPointCount: 2));
        Assert.Equal([3, 4], recent.Points.Select(point => point.LogicalIndex));
        Assert.Empty(history.Build(4, ReplayTrailMode.Persistent, minimumLogicalIndex: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => history.Build(4, ReplayTrailMode.Recent, 121));
    }

    [Fact]
    public void Persistent_preserves_every_reported_point_for_the_evidence_layer()
    {
        const int reportCount = 700;
        var replay = new FakeReplay(Enumerable.Range(0, reportCount)
            .Select(index => Snapshot(
                index,
                Contact(
                    index,
                    index == 0 ? TouchStatus.Enter : TouchStatus.Move,
                    (ushort)(10 + index))))
            .ToArray());

        var trail = Assert.Single(ReplayTrailHistory.Create(replay).Build(reportCount - 1, ReplayTrailMode.Persistent));

        Assert.Equal(reportCount, trail.Points.Count);
        Assert.Equal(Enumerable.Range(0, reportCount), trail.Points.Select(point => point.LogicalIndex));
    }

    [Fact]
    public void Hundred_thousand_points_are_losslessly_chunked_with_continuous_line_boundaries()
    {
        const int reportCount = 100_003;
        var points = Enumerable.Range(0, reportCount)
            .Select(index => new ReplayTrailPoint(
                (ushort)(index % 4096),
                (ushort)((index * 7) % 2048),
                index == 0 ? TouchStatus.Enter : TouchStatus.Move,
                index))
            .ToArray();
        var chunks = ReplayTrailChunker.Enumerate(
            new ReplayContactTrail(1, TouchType.Finger, points, IsComplete: true))
            .ToArray();

        Assert.Equal(reportCount, chunks.Sum(chunk => chunk.Count));
        Assert.Equal(49, chunks.Length);
        Assert.Null(chunks[0].LeadingOffset);
        Assert.All(chunks, chunk => Assert.True(chunk.IsCacheable));
        for (var index = 1; index < chunks.Length; index++)
        {
            Assert.Equal(
                chunks[index - 1][chunks[index - 1].Count - 1],
                chunks[index].LeadingPoint);
        }

        var activeTail = ReplayTrailChunker.Enumerate(
            new ReplayContactTrail(1, TouchType.Finger, new ArraySegment<ReplayTrailPoint>(points, 0, reportCount - 1)))
            .ToArray();
        Assert.False(activeTail[^1].IsCacheable);
    }

    [Fact]
    public void Hundred_thousand_report_points_survive_random_seeks_without_rebuilding_history()
    {
        const int reportCount = 100_003;
        var snapshots = Enumerable.Range(0, reportCount)
            .Select(index => (ITouchReplaySnapshot)Snapshot(
                index,
                Contact(index, index == 0 ? TouchStatus.Enter : TouchStatus.Move, (ushort)(index % 4096))))
            .ToArray();

        var beforeCreate = GC.GetAllocatedBytesForCurrentThread();
        var history = ReplayTrailHistory.Create(snapshots);
        var createBytes = GC.GetAllocatedBytesForCurrentThread() - beforeCreate;
        var statistics = history.Statistics;
        var firstSeekBytes = MeasureRandomBuilds(history, reportCount, 250);
        var secondSeekBytes = MeasureRandomBuilds(history, reportCount, 250);
        Console.WriteLine($"trail-allocation create={createBytes} random-seeks={firstSeekBytes}");

        var complete = Assert.Single(history.Build(reportCount - 1, ReplayTrailMode.Persistent));
        Assert.Equal(reportCount, complete.Points.Count);
        Assert.Equal(new ReplayTrailStorageStatistics(1, reportCount, 48), statistics);
        Assert.Equal(statistics, history.Statistics);
        Assert.InRange(secondSeekBytes, 0, firstSeekBytes + 16_384);

        var early = Assert.Single(history.Build(4095, ReplayTrailMode.Persistent));
        var earlyChunks = ReplayTrailChunker.Enumerate(early).ToArray();
        var completeChunks = ReplayTrailChunker.Enumerate(complete).ToArray();
        Assert.True(earlyChunks[0].IsCacheable);
        Assert.Same(earlyChunks[0].Source, completeChunks[0].Source);
        Assert.Equal(earlyChunks[0].Offset, completeChunks[0].Offset);
        Assert.Equal(earlyChunks[0].Count, completeChunks[0].Count);
    }

    private static long MeasureRandomBuilds(ReplayTrailHistory history, int frameCount, int buildCount)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        IReadOnlyList<ReplayContactTrail> last = [];
        for (var seek = 0; seek < buildCount; seek++)
        {
            var logicalIndex = (int)((long)seek * 104_729 % frameCount);
            last = history.Build(logicalIndex, ReplayTrailMode.Persistent);
        }
        GC.KeepAlive(last);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static FakeReplay Replay()
    {
        var contacts = new[]
        {
            Contact(0, TouchStatus.Enter, 10),
            Contact(1, TouchStatus.Move, 20),
            Contact(2, TouchStatus.Break, 20),
            Contact(3, TouchStatus.Enter, 100),
            Contact(4, TouchStatus.Move, 110),
        };
        return new FakeReplay(contacts.Select((contact, index) => Snapshot(index, contact)).ToArray());
    }

    private static ReplayContact Contact(int index, TouchStatus status, ushort x) =>
        new(1, TouchType.Finger, status, x, (ushort)(x + 10), $"source-{index}");

    private static FakeSnapshot Snapshot(int index, ReplayContact contact)
    {
        var source = new SourceRecord(
            index,
            contact.SourceRecordId,
            DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(index * 10),
            BusOperation.Paint,
            "TP",
            1,
            80,
            [],
            "",
            new SourceLocation(index * 10, index + 1));
        return new FakeSnapshot(
            index,
            source,
            new ReplayTimelineEntry(
                index,
                TimeSpan.FromMilliseconds(index * 10),
                TimeSpan.FromMilliseconds(index * 10),
                TimeSpan.FromMilliseconds(index * 10),
                index == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(10),
                false,
                false),
            [contact]);
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

    private sealed class FakeReplay(IReadOnlyList<ITouchReplaySnapshot> snapshots) : ITouchReplaySession
    {
        public int Count => snapshots.Count;
        public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(10);
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; } = snapshots.Select(item => item.Timeline).ToArray();
        public IReadOnlyList<ReplayDiagnostic> Diagnostics => [];
        public IReadOnlyList<ReplayContact> AllReportedContacts { get; } = snapshots.SelectMany(item => item.ReportedContacts).ToArray();
        public ITouchReplaySnapshot Seek(int logicalIndex) => snapshots[logicalIndex];
    }
}
