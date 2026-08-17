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
