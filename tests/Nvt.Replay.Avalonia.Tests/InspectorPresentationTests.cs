using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class InspectorPresentationTests
{
    [Fact]
    public void Compare_contacts_prefer_this_frames_report_and_keep_other_host_contacts()
    {
        var source = Source();
        var snapshot = Snapshot(
            source,
            reported:
            [
                Contact(1, TouchType.Finger, TouchStatus.Break, 120, 220),
                Contact(3, TouchType.Palm, TouchStatus.NoFinger, 330, 430),
            ],
            host:
            [
                Contact(2, TouchType.Glove, TouchStatus.Move, 210, 310),
                Contact(3, TouchType.Palm, TouchStatus.Move, 300, 400),
            ]);

        var rows = InspectorPresentationBuilder.BuildContacts(snapshot, ReplayRenderMode.Compare);

        Assert.Equal([1, 2, 3], rows.Select(row => (int)row.Id));
        Assert.Equal(TouchStatus.NoFinger, rows.Single(row => row.Id == 3).Status);
        Assert.True(rows.Single(row => row.Id == 3).ReportedThisFrame);
        Assert.False(rows.Single(row => row.Id == 2).ReportedThisFrame);
    }

    [Fact]
    public void Summary_uses_active_host_state_and_exposes_coordinate_free_global_palm()
    {
        var source = Source();
        var snapshot = Snapshot(
            source,
            reported: [],
            host:
            [
                Contact(1, TouchType.Finger, TouchStatus.Move, 100, 200),
                Contact(2, TouchType.Glove, TouchStatus.Enter, 300, 400),
                Contact(4, TouchType.Finger, TouchStatus.Break, 500, 600),
            ],
            globalPalm: true);

        var summary = InspectorPresentationBuilder.BuildSummary(snapshot);

        Assert.Equal(1, summary.Fingers);
        Assert.Equal(1, summary.Gloves);
        Assert.Equal(1, summary.Palms);
        Assert.Equal(3, summary.Total);
    }

    [Fact]
    public void Contact_rows_are_capped_at_the_supported_ten_ids()
    {
        var source = Source();
        var contacts = Enumerable.Range(1, 12)
            .Select(id => Contact((byte)id, TouchType.Finger, TouchStatus.Move, (ushort)(id * 10), (ushort)(id * 20)))
            .ToArray();
        var snapshot = Snapshot(source, contacts, contacts);

        var rows = InspectorPresentationBuilder.BuildContacts(snapshot, ReplayRenderMode.HostState);

        Assert.Equal(10, rows.Count);
        Assert.Equal(10, rows[^1].Id);
    }

    private static ReplayContact Contact(byte id, TouchType type, TouchStatus status, ushort x, ushort y) =>
        new(id, type, status, x, y, $"source-{id}");

    private static SourceRecord Source() =>
        new(1, "source", null, BusOperation.Paint, "TP", 0x99000, 80, [0x00], "", new SourceLocation(0, 1));

    private static FakeSnapshot Snapshot(
        SourceRecord source,
        IReadOnlyList<ReplayContact> reported,
        IReadOnlyList<ReplayContact> host,
        bool globalPalm = false) =>
        new(
            0,
            source,
            new ReplayTimelineEntry(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, false, false),
            reported,
            host,
            globalPalm);

    private sealed record FakeSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        ReplayTimelineEntry Timeline,
        IReadOnlyList<ReplayContact> ReportedContacts,
        IReadOnlyList<ReplayContact> HostContacts,
        bool GlobalPalm) : ITouchReplaySnapshot
    {
        public object DecodedFrame => new();
        public IReadOnlyList<SourceRecord> PhysicalRecords => [PrimarySource];
        public bool HostStateUpdated => true;
    }
}
