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

    [Fact]
    public void Review_options_update_in_place_and_survive_marker_session_rebuilds()
    {
        var frames = Frames(2);
        var workspace = new ReviewInspectorWorkspace(
            frames,
            [Diagnostic("ALARM", frames[1].PrimarySource, DiagnosticSeverity.Alarm)],
            reviewOptions: new ReviewSessionOptions(PauseOnAlarm: true, PauseOnQaFail: false));
        var originalSession = workspace.ReviewSession;
        var originalRevision = workspace.ReviewSessionRevision;

        Assert.True(workspace.SetReviewOptions(new ReviewSessionOptions(PauseOnAlarm: false, PauseOnQaFail: true)));
        Assert.Same(originalSession, workspace.ReviewSession);
        Assert.Equal(originalRevision, workspace.ReviewSessionRevision);
        Assert.Equal(new ReviewSessionOptions(false, true), workspace.ReviewOptions);
        Assert.False(workspace.SetReviewOptions(new ReviewSessionOptions(false, true)));
        Assert.Equal(originalRevision, workspace.ReviewSessionRevision);

        workspace.SelectFrame(1);
        workspace.AddMarker();

        Assert.NotSame(originalSession, workspace.ReviewSession);
        Assert.Equal(originalRevision + 1, workspace.ReviewSessionRevision);
        Assert.Equal(new ReviewSessionOptions(false, true), workspace.ReviewOptions);
    }

    [Fact]
    public void Human_review_mutations_refresh_visible_projection_without_rebuilding_session()
    {
        var frames = Frames(1);
        var workspace = new ReviewInspectorWorkspace(
            frames,
            [Diagnostic("WARNING", frames[0].PrimarySource, DiagnosticSeverity.Warning)]);
        var group = Assert.Single(workspace.VisibleGroups);
        workspace.SelectFinding(group.Id);
        var session = workspace.ReviewSession;
        var revision = workspace.ReviewSessionRevision;
        var reportRevision = workspace.ReportRevision;

        var acknowledged = workspace.AcknowledgeFinding(group.Id);
        Assert.Equal(ReviewWorkflowState.Acknowledged, acknowledged.WorkflowState);
        Assert.Equal(ReviewWorkflowState.Acknowledged, workspace.SelectedFinding?.WorkflowState);
        Assert.Equal(reportRevision + 1, workspace.ReportRevision);
        reportRevision = workspace.ReportRevision;
        workspace.AcknowledgeFinding(group.Id);
        Assert.Equal(reportRevision, workspace.ReportRevision);

        var expected = workspace.SetFindingDisposition(group.Id, ReviewDisposition.Expected);
        Assert.Equal(ReviewDisposition.Expected, expected.Disposition);
        Assert.Equal(reportRevision + 1, workspace.ReportRevision);
        reportRevision = workspace.ReportRevision;
        workspace.SetFindingDisposition(group.Id, ReviewDisposition.Expected);
        Assert.Equal(reportRevision, workspace.ReportRevision);
        var resolved = workspace.ResolveFinding(group.Id);
        Assert.Equal(ReviewWorkflowState.Resolved, resolved.WorkflowState);
        Assert.Equal(reportRevision + 1, workspace.ReportRevision);
        reportRevision = workspace.ReportRevision;
        Assert.Throws<InvalidOperationException>(() => workspace.ResolveFinding(group.Id));
        Assert.Equal(reportRevision, workspace.ReportRevision);
        Assert.Same(session, workspace.ReviewSession);
        Assert.Equal(revision, workspace.ReviewSessionRevision);
        Assert.Equal(group.Id, workspace.SelectedFindingGroupId);
    }

    [Fact]
    public void Marker_metadata_rebuilds_once_per_change_and_keeps_evidence_availability_semantics()
    {
        var workspace = new ReviewInspectorWorkspace(
            Frames(1),
            [],
            evidenceExists: path => path.Equals("present.log", StringComparison.Ordinal));
        var markerView = workspace.Markers;
        var marker = workspace.AddMarker();
        var revision = workspace.ReviewSessionRevision;

        var withQa = workspace.SetMarkerQaCaseIds(marker.Id, [" QA-17 ", "qa-17", "QA-22"]);

        Assert.Equal(["QA-17", "QA-22"], withQa.QaCaseIds);
        Assert.Same(markerView, workspace.Markers);
        Assert.Equal(revision + 1, workspace.ReviewSessionRevision);
        revision = workspace.ReviewSessionRevision;

        workspace.SetMarkerQaCaseIds(marker.Id, ["QA-17", "QA-22"]);
        Assert.Equal(revision, workspace.ReviewSessionRevision);

        workspace.AddMarkerEvidence(marker.Id, new ReplayEvidenceReference(
            "evidence-present",
            ReplayEvidenceKind.KernelLog,
            "present.log",
            Label: "Kernel"));
        Assert.Equal(revision + 1, workspace.ReviewSessionRevision);
        revision = workspace.ReviewSessionRevision;
        workspace.AddMarkerEvidence(marker.Id, new ReplayEvidenceReference(
            "evidence-missing",
            ReplayEvidenceKind.FirmwareLog,
            "missing.log",
            Label: "FW"));
        Assert.Equal(revision + 1, workspace.ReviewSessionRevision);

        var group = Assert.Single(workspace.VisibleGroups);
        var details = Assert.Single(group.Occurrences).Diagnostic.Details;
        Assert.Equal("QA-17, QA-22", details?.GetValueOrDefault("qa_case_ids"));
        Assert.Equal(
            "KernelLog:Kernel:available; FirmwareLog:FW:missing",
            details?.GetValueOrDefault("evidence"));
        revision = workspace.ReviewSessionRevision;
        Assert.Throws<ArgumentException>(() => workspace.AddMarkerEvidence(marker.Id, new ReplayEvidenceReference(
            "evidence-present",
            ReplayEvidenceKind.Other,
            "another.log")));
        Assert.Equal(revision, workspace.ReviewSessionRevision);
    }

    [Fact]
    public void Sidecar_marker_replace_imports_state_once_and_preserves_only_stable_selection()
    {
        var frames = Frames(3);
        var createdAt = DateTimeOffset.UnixEpoch;
        var selected = new ReplayMarker("marker-stable", "Before", 1, 1, createdAt);
        var removed = new ReplayMarker("marker-removed", "Removed", 2, 2, createdAt.AddSeconds(1));
        var workspace = new ReviewInspectorWorkspace(
            frames,
            [],
            markers: [selected, removed],
            reviewOptions: new ReviewSessionOptions(false, true));
        var markerView = workspace.Markers;
        Assert.True(workspace.SelectMarker(selected.Id));
        var revision = workspace.ReviewSessionRevision;
        var replacement = new[]
        {
            selected with { Label = "Loaded stable" },
            new ReplayMarker("marker-loaded", "Loaded new", 0, 0, createdAt.AddSeconds(2)),
        };
        var selectedGroupId = $"ANNOTATION_MARKER:{selected.Id}";
        var importedState = new[]
        {
            new ReviewStateSnapshot(selectedGroupId, ReviewWorkflowState.Acknowledged, ReviewDisposition.Expected),
            new ReviewStateSnapshot("STALE_GROUP", ReviewWorkflowState.Resolved, ReviewDisposition.FalsePositive),
        };

        var result = workspace.ReplaceMarkersAndImportState(replacement, importedState);

        Assert.True(result.Changed);
        Assert.True(result.MarkersChanged);
        Assert.True(result.ReviewStateChanged);
        Assert.Equal(2, result.MarkerCount);
        Assert.Equal(["STALE_GROUP"], result.UnresolvedReviewGroupIds);
        Assert.Equal(revision + 1, workspace.ReviewSessionRevision);
        Assert.Same(markerView, workspace.Markers);
        Assert.Equal(["marker-stable", "marker-loaded"], workspace.Markers.Select(marker => marker.Id));
        Assert.Equal("Loaded stable", workspace.SelectedMarker?.Label);
        Assert.Equal(selectedGroupId, workspace.SelectedFindingGroupId);
        Assert.Equal(ReviewWorkflowState.Acknowledged, workspace.SelectedFinding?.WorkflowState);
        Assert.Equal(ReviewDisposition.Expected, workspace.SelectedFinding?.Disposition);
        Assert.Equal(new ReviewSessionOptions(false, true), workspace.ReviewOptions);

        revision = workspace.ReviewSessionRevision;
        var noOp = workspace.ReplaceMarkersAndImportState(replacement, importedState);
        Assert.False(noOp.Changed);
        Assert.False(noOp.MarkersChanged);
        Assert.False(noOp.ReviewStateChanged);
        Assert.Equal(["STALE_GROUP"], noOp.UnresolvedReviewGroupIds);
        Assert.Equal(revision, workspace.ReviewSessionRevision);

        var withoutSelection = workspace.ReplaceMarkersAndImportState([replacement[1]], []);
        Assert.True(withoutSelection.MarkersChanged);
        Assert.Null(workspace.SelectedMarkerId);
        Assert.Null(workspace.SelectedFindingGroupId);
        Assert.Equal(revision + 1, workspace.ReviewSessionRevision);
        revision = workspace.ReviewSessionRevision;

        Assert.Throws<ArgumentException>(() => workspace.ReplaceMarkersAndImportState(
            [replacement[1], replacement[1]],
            []));
        Assert.Equal(revision, workspace.ReviewSessionRevision);
        Assert.Equal(["marker-loaded"], workspace.Markers.Select(marker => marker.Id));
    }

    [Fact]
    public void Sidecar_review_state_is_replaced_instead_of_merged_and_empty_replace_is_a_no_op()
    {
        var frames = Frames(2);
        var diagnostics = new[]
        {
            Diagnostic("FIRST_WARNING", frames[0].PrimarySource, DiagnosticSeverity.Warning),
            Diagnostic("SECOND_WARNING", frames[1].PrimarySource, DiagnosticSeverity.Warning),
        };
        var workspace = new ReviewInspectorWorkspace(frames, diagnostics);
        var first = Assert.Single(workspace.VisibleGroups, group => group.Code == "FIRST_WARNING");
        var second = Assert.Single(workspace.VisibleGroups, group => group.Code == "SECOND_WARNING");
        workspace.AcknowledgeFinding(first.Id);
        workspace.SetFindingDisposition(first.Id, ReviewDisposition.Expected);
        workspace.AcknowledgeFinding(second.Id);
        var revision = workspace.ReviewSessionRevision;

        var replaced = workspace.ReplaceMarkersAndImportState(
            [],
            [new ReviewStateSnapshot(first.Id, ReviewWorkflowState.Acknowledged, ReviewDisposition.FalsePositive)]);

        Assert.True(replaced.ReviewStateChanged);
        Assert.False(replaced.MarkersChanged);
        Assert.Equal(revision + 1, workspace.ReviewSessionRevision);
        Assert.Equal(ReviewWorkflowState.Acknowledged, workspace.ReviewSession.Find(first.Id).WorkflowState);
        Assert.Equal(ReviewDisposition.FalsePositive, workspace.ReviewSession.Find(first.Id).Disposition);
        Assert.Equal(ReviewWorkflowState.Open, workspace.ReviewSession.Find(second.Id).WorkflowState);
        Assert.Equal(ReviewDisposition.None, workspace.ReviewSession.Find(second.Id).Disposition);

        revision = workspace.ReviewSessionRevision;
        var cleared = workspace.ReplaceMarkersAndImportState([], []);
        Assert.True(cleared.ReviewStateChanged);
        Assert.Equal(ReviewWorkflowState.Open, workspace.ReviewSession.Find(first.Id).WorkflowState);
        Assert.Empty(workspace.ReviewSession.ExportState());
        Assert.Equal(revision + 1, workspace.ReviewSessionRevision);

        revision = workspace.ReviewSessionRevision;
        var noOp = workspace.ReplaceMarkersAndImportState([], []);
        Assert.False(noOp.Changed);
        Assert.Equal(revision, workspace.ReviewSessionRevision);
    }

    [Fact]
    public void Malformed_sidecar_markers_are_rejected_atomically()
    {
        var frames = Frames(2);
        var workspace = new ReviewInspectorWorkspace(frames, []);
        var original = workspace.AddMarker("Original");
        var revision = workspace.ReviewSessionRevision;
        var createdAt = DateTimeOffset.UnixEpoch;
        var invalid = new IReadOnlyList<ReplayMarker>[]
        {
            [new ReplayMarker("range", "Range", 0, 1, createdAt)],
            [new ReplayMarker("negative", "Negative", -1, -1, createdAt)],
            [new ReplayMarker("outside", "Outside", frames.Length, frames.Length, createdAt)],
            [new ReplayMarker("", "Empty ID", 0, 0, createdAt)],
            [new ReplayMarker("empty-label", " ", 0, 0, createdAt)],
            [new ReplayMarker("duplicate", "One", 0, 0, createdAt), new ReplayMarker("duplicate", "Two", 1, 1, createdAt)],
            [new ReplayMarker("one", "Duplicate", 0, 0, createdAt), new ReplayMarker("two", " duplicate ", 1, 1, createdAt)],
            [new ReplayMarker("qa-empty", "QA empty", 0, 0, createdAt, QaCaseIds: [" "])],
            [new ReplayMarker("qa-duplicate", "QA duplicate", 0, 0, createdAt, QaCaseIds: ["QA-1", "qa-1"])],
            [new ReplayMarker("evidence-id", "Evidence ID", 0, 0, createdAt, Evidence:
                [new ReplayEvidenceReference("", ReplayEvidenceKind.Other, "trace.log")])],
            [new ReplayMarker("evidence-path", "Evidence path", 0, 0, createdAt, Evidence:
                [new ReplayEvidenceReference("evidence", ReplayEvidenceKind.Other, " ")])],
            [new ReplayMarker("evidence-hash", "Evidence hash", 0, 0, createdAt, Evidence:
                [new ReplayEvidenceReference("evidence", ReplayEvidenceKind.Other, "trace.log", "not-a-sha")])],
            [new ReplayMarker("evidence-one", "Evidence one", 0, 0, createdAt, Evidence:
                [new ReplayEvidenceReference("duplicate-evidence", ReplayEvidenceKind.Other, "one.log")]),
             new ReplayMarker("evidence-two", "Evidence two", 1, 1, createdAt, Evidence:
                [new ReplayEvidenceReference("duplicate-evidence", ReplayEvidenceKind.Other, "two.log")])],
        };

        foreach (var replacement in invalid)
        {
            Assert.Throws<ArgumentException>(() => workspace.ReplaceMarkersAndImportState(replacement, []));
            Assert.Equal(revision, workspace.ReviewSessionRevision);
            Assert.Same(original, Assert.Single(workspace.Markers));
            Assert.Equal(original.Id, workspace.SelectedMarkerId);
        }
    }

    [Fact]
    public void Navigation_uses_one_cached_index_for_one_hundred_thousand_occurrences()
    {
        var frames = Frames(1);
        var diagnostic = Diagnostic("REPEATED_WARNING", frames[0].PrimarySource, DiagnosticSeverity.Warning);
        var diagnostics = Enumerable.Repeat(diagnostic, 100_000).ToArray();
        var workspace = new ReviewInspectorWorkspace(frames, diagnostics);
        var revision = workspace.NavigationIndexRevision;
        Assert.Equal(100_000, Assert.Single(workspace.VisibleGroups).Occurrences.Count);

        workspace.NavigateFinding(ReviewFindingDirection.Next);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 250; index++)
            Assert.NotNull(workspace.NavigateFinding(ReviewFindingDirection.Next));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(revision, workspace.NavigationIndexRevision);
        Assert.True(allocated < 16_000_000,
            $"Cached navigation allocated {allocated:N0} bytes for 250 operations.");
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
