using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class ReviewInspectorWorkspaceTests
{
    [Fact]
    public void Markers_are_single_frame_stable_and_independent_of_loop_range()
    {
        var frames = Frames(5);
        var workspace = new ReviewInspectorWorkspace(frames, []);
        var loop = new ReplayLoopRange(1, 4);
        workspace.SelectFrame(3);

        var marker = workspace.AddMarker();

        Assert.Equal(3, marker.StartLogicalIndex);
        Assert.Equal(3, marker.EndLogicalIndex);
        Assert.NotEqual((loop.StartLogicalIndex, loop.EndLogicalIndex),
            (marker.StartLogicalIndex, marker.EndLogicalIndex));
        Assert.False(marker.IsRange);
        Assert.Equal(marker.Id, workspace.SelectedMarkerId);

        var renamed = workspace.RenameMarker(marker.Id, "Startup touch");
        Assert.Equal(marker.Id, renamed.Id);
        Assert.Equal(marker.CreatedAt, renamed.CreatedAt);
        Assert.Equal("Startup touch", renamed.Label);

        var overlapping = workspace.AddMarker();
        Assert.NotEqual(renamed.Id, overlapping.Id);
        Assert.Equal(3, overlapping.StartLogicalIndex);
        Assert.True(workspace.RemoveMarker(overlapping.Id));
        Assert.Equal(renamed.Id, Assert.Single(workspace.Markers).Id);
        Assert.Equal(1, workspace.ClearMarkers());
        Assert.Empty(workspace.Markers);
        Assert.Null(workspace.SelectedMarkerId);
        Assert.Null(workspace.SelectedFindingGroupId);
    }

    [Fact]
    public void Finding_navigation_and_occurrence_selection_wrap_in_timeline_order()
    {
        var frames = Frames(3);
        var repeatedAtZero = Diagnostic("REPEATED", frames[0].PrimarySource, DiagnosticSeverity.Warning);
        var middle = Diagnostic("MIDDLE", frames[1].PrimarySource, DiagnosticSeverity.Warning);
        var repeatedAtTwo = Diagnostic("REPEATED", frames[2].PrimarySource, DiagnosticSeverity.Warning);
        var diagnostics = new[] { repeatedAtZero, middle, repeatedAtTwo };
        var workspace = new ReviewInspectorWorkspace(frames, diagnostics);

        Assert.Equal(1, workspace.NavigateFinding(ReviewFindingDirection.Next)?.LogicalIndex);
        Assert.Equal(2, workspace.NavigateFinding(ReviewFindingDirection.Next)?.LogicalIndex);
        Assert.Equal(0, workspace.NavigateFinding(ReviewFindingDirection.Next)?.LogicalIndex);
        Assert.Equal(2, workspace.NavigateFinding(ReviewFindingDirection.Previous)?.LogicalIndex);

        var repeated = Assert.Single(workspace.VisibleGroups, group => group.Code == "REPEATED");
        var selected = workspace.SelectFinding(repeated.Id, occurrenceIndex: 1);
        Assert.Equal(2, selected.LogicalIndex);
        Assert.Equal(2, workspace.CurrentLogicalIndex);
        Assert.Same(repeatedAtTwo, selected.Diagnostic);

        workspace.ClearFindingSelection();
        Assert.Null(workspace.SelectedFindingGroupId);
        Assert.Null(workspace.SelectedOccurrenceId);
        Assert.Null(workspace.SelectedOccurrence);
        Assert.Equal(2, workspace.CurrentLogicalIndex);
        Assert.Same(diagnostics, workspace.BaseDiagnostics);
    }

    [Fact]
    public void Filter_hides_invalid_selection_without_cloning_diagnostics()
    {
        var frames = Frames(3);
        var warning = Diagnostic("WARNING", frames[0].PrimarySource, DiagnosticSeverity.Warning);
        var asil = Diagnostic("COMMON_ASIL_ALARM", frames[1].PrimarySource, DiagnosticSeverity.Alarm);
        var info = Diagnostic("QA_CASE_PASS", frames[2].PrimarySource, DiagnosticSeverity.Info);
        var diagnostics = new[] { warning, asil, info };
        var workspace = new ReviewInspectorWorkspace(frames, diagnostics);
        var infoGroup = Assert.Single(workspace.VisibleGroups, group => group.Code == "QA_CASE_PASS");
        workspace.SelectFinding(infoGroup.Id);

        workspace.SetFilter(ReviewQueueFilter.Asil);

        var visible = Assert.Single(workspace.VisibleGroups);
        Assert.Equal("COMMON_ASIL_ALARM", visible.Code);
        Assert.Same(asil, Assert.Single(visible.Occurrences).Diagnostic);
        Assert.Null(workspace.SelectedFindingGroupId);
        Assert.Null(workspace.SelectedOccurrenceId);
        Assert.Same(diagnostics, workspace.BaseDiagnostics);
    }

    [Fact]
    public void Inspector_caps_contacts_preserves_raw_and_clears_unavailable_selection()
    {
        var firstContacts = Enumerable.Range(1, 12)
            .Select(id => Contact((byte)id))
            .ToArray();
        var first = Snapshot(0, firstContacts);
        var secondContact = Contact(2);
        var second = Snapshot(1, [secondContact]);
        var frames = new ITouchReplaySnapshot[] { first, second };
        var diagnostic = Diagnostic("WARNING", first.PrimarySource, DiagnosticSeverity.Warning);
        var diagnostics = new[] { diagnostic };
        var workspace = new ReviewInspectorWorkspace(
            frames,
            diagnostics,
            renderMode: ReplayRenderMode.HostState);

        Assert.Same(frames, workspace.Frames);
        Assert.Same(first, workspace.CurrentSnapshot);
        Assert.Same(first.PrimarySource, workspace.CurrentSource);
        Assert.Equal(10, workspace.CurrentPresentation?.Contacts.Count);
        Assert.True(workspace.SelectContact(10));
        Assert.Equal<byte?>(10, workspace.SelectedContactId);
        Assert.False(workspace.SelectContact(11));
        Assert.Null(workspace.SelectedContactId);

        Assert.True(workspace.SelectContact(10));
        workspace.SelectFrame(1);
        Assert.Null(workspace.SelectedContactId);
        Assert.True(workspace.SelectContact(2));
        Assert.Equal<byte?>(2, workspace.SelectedContact?.Id);
        workspace.ClearContactSelection();
        Assert.Null(workspace.SelectedContactId);

        Assert.Same(second, workspace.CurrentSnapshot);
        Assert.Same(second.PrimarySource, workspace.CurrentSource);
        Assert.Same(second.PrimarySource.Data, workspace.CurrentSource?.Data);
        Assert.Equal(second.PrimarySource.RawText, workspace.CurrentSource?.RawText);
        Assert.Contains("ID 2", workspace.BuildCopyPayload(), StringComparison.Ordinal);
        Assert.Same(diagnostics, workspace.BaseDiagnostics);
    }

    private static ITouchReplaySnapshot[] Frames(int count) => Enumerable.Range(0, count)
        .Select(index => (ITouchReplaySnapshot)Snapshot(index, [Contact((byte)(index + 1))]))
        .ToArray();

    private static FakeSnapshot Snapshot(int logicalIndex, IReadOnlyList<ReplayContact> contacts)
    {
        IReadOnlyList<byte> data = [(byte)logicalIndex, 0xAA, 0x55];
        var source = new SourceRecord(
            logicalIndex,
            $"source-{logicalIndex}",
            DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(logicalIndex * 10),
            BusOperation.Paint,
            "TP",
            0x01,
            data.Count,
            data,
            $"raw frame {logicalIndex}",
            new SourceLocation(logicalIndex * 100, logicalIndex + 1));
        var displayTime = TimeSpan.FromMilliseconds(logicalIndex * 10);
        return new FakeSnapshot(
            logicalIndex,
            source,
            new ReplayTimelineEntry(
                logicalIndex,
                displayTime,
                displayTime,
                displayTime,
                logicalIndex == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(10),
                false,
                false),
            contacts,
            contacts);
    }

    private static ReplayContact Contact(byte id) => new(
        id,
        id == 2 ? TouchType.Glove : TouchType.Finger,
        TouchStatus.Move,
        (ushort)(id * 10),
        (ushort)(id * 20),
        $"contact-{id}");

    private static ReplayDiagnostic Diagnostic(
        string code,
        SourceRecord source,
        DiagnosticSeverity severity) => new(
            severity,
            code,
            code,
            source.StableId,
            source.Location);

    private sealed record FakeSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        ReplayTimelineEntry Timeline,
        IReadOnlyList<ReplayContact> ReportedContacts,
        IReadOnlyList<ReplayContact> HostContacts) : ITouchReplaySnapshot
    {
        public object DecodedFrame { get; } = new();
        public IReadOnlyList<SourceRecord> PhysicalRecords => [PrimarySource];
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }
}
