// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Core;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

/// <summary>Characterizes review projection and navigation through the workspace and index.</summary>
public sealed class ReviewPresentationIndexTests
{
    /// <summary>The initial snapshot matches the former empty arrays and ordinal dictionaries.</summary>
    [Fact]
    public void EmptyIndexHasEmptyCollectionsAndSearchesReturnZero()
    {
        var index = ReviewPresentationIndex.Empty;

        Assert.Empty(index.VisibleGroups);
        Assert.Empty(index.VisibleGroupsById);
        Assert.Empty(index.NavigableOccurrences);
        Assert.Empty(index.NavigablePositionByOccurrenceId);
        Assert.Same(StringComparer.Ordinal, Assert.IsType<Dictionary<string, ReviewEventGroup>>(index.VisibleGroupsById).Comparer);
        Assert.Same(StringComparer.Ordinal, Assert.IsType<Dictionary<string, int>>(index.NavigablePositionByOccurrenceId).Comparer);
        Assert.Equal(0, index.FirstOccurrenceAfter(int.MinValue));
        Assert.Equal(0, index.FirstOccurrenceAtOrAfter(int.MaxValue));
    }

    /// <summary>The exclusive search returns the first greater index or the collection length.</summary>
    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 3)]
    [InlineData(5, 3)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(int.MaxValue, 4)]
    public void IndexFindsFirstOccurrenceAfterAtBoundaries(int logicalIndex, int expectedPosition)
    {
        var index = CreateIndex();

        var position = index.FirstOccurrenceAfter(logicalIndex);

        Assert.Equal(expectedPosition, position);
    }

    /// <summary>The inclusive search returns the first equal or greater index, including ties.</summary>
    [Theory]
    [InlineData(int.MinValue, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(7, 3)]
    [InlineData(8, 4)]
    [InlineData(int.MaxValue, 4)]
    public void IndexFindsFirstOccurrenceAtOrAfterAtBoundaries(int logicalIndex, int expectedPosition)
    {
        var index = CreateIndex();

        var position = index.FirstOccurrenceAtOrAfter(logicalIndex);

        Assert.Equal(expectedPosition, position);
    }

    /// <summary>The snapshot retains its source references and exact ordinal navigation positions.</summary>
    [Fact]
    public void IndexRetainsSourceReferencesAndOrdinalPositionLookups()
    {
        var index = CreateIndex();

        Assert.Same(index.VisibleGroups[0], index.VisibleGroupsById["B"]);
        Assert.Same(index.VisibleGroups[0], index.NavigableOccurrences[1].Group);
        Assert.Same(index.VisibleGroups[0].Occurrences[1], index.NavigableOccurrences[1].Occurrence);
        Assert.Equal(1, index.NavigableOccurrences[1].OccurrenceIndex);
        Assert.Equal(0, index.NavigablePositionByOccurrenceId["first"]);
        Assert.Equal(1, index.NavigablePositionByOccurrenceId["second"]);
        Assert.Equal(2, index.NavigablePositionByOccurrenceId["middle"]);
        Assert.Equal(3, index.NavigablePositionByOccurrenceId["last"]);
        Assert.False(index.VisibleGroupsById.ContainsKey("b"));
        Assert.False(index.NavigablePositionByOccurrenceId.ContainsKey("SECOND"));
        Assert.False(index.NavigablePositionByOccurrenceId.ContainsKey("missing"));
    }

    /// <summary>An empty workspace has no visible groups or navigation targets.</summary>
    [Fact]
    public void EmptyWorkspaceHasNoVisibleOrNavigableFindings()
    {
        var workspace = new ReviewInspectorWorkspace([], []);

        Assert.Empty(workspace.VisibleGroups);
        Assert.Null(workspace.NavigateFinding(ReviewFindingDirection.Next));
        Assert.Null(workspace.NavigateFinding(ReviewFindingDirection.Previous));
        Assert.Null(workspace.SelectedFinding);
    }

    /// <summary>Navigation orders ties by ordinal group ID and then original occurrence index.</summary>
    [Fact]
    public void WorkspaceNavigatesByLogicalIndexThenOrdinalGroupIdThenOccurrenceIndex()
    {
        var workspace = CreateWorkspace();
        workspace.SelectFrame(0);

        string?[] actual =
        [
            workspace.NavigateFinding(ReviewFindingDirection.Next)?.Id,
            workspace.NavigateFinding(ReviewFindingDirection.Next)?.Id,
            workspace.NavigateFinding(ReviewFindingDirection.Next)?.Id,
            workspace.NavigateFinding(ReviewFindingDirection.Next)?.Id,
            workspace.NavigateFinding(ReviewFindingDirection.Next)?.Id,
            workspace.NavigateFinding(ReviewFindingDirection.Next)?.Id,
        ];

        Assert.Equal(["B@source-2#2", "B@source-2#4", "a@source-2#1", "M@source-4#3", "A@source-7#0", "B@source-2#2"], actual.AsEnumerable());
    }

    /// <summary>Unselected navigation excludes the current frame and wraps at either end.</summary>
    [Theory]
    [InlineData(0, ReviewFindingDirection.Next, "B@source-2#2")]
    [InlineData(0, ReviewFindingDirection.Previous, "A@source-7#0")]
    [InlineData(2, ReviewFindingDirection.Next, "M@source-4#3")]
    [InlineData(2, ReviewFindingDirection.Previous, "A@source-7#0")]
    [InlineData(3, ReviewFindingDirection.Next, "M@source-4#3")]
    [InlineData(3, ReviewFindingDirection.Previous, "a@source-2#1")]
    [InlineData(4, ReviewFindingDirection.Next, "A@source-7#0")]
    [InlineData(4, ReviewFindingDirection.Previous, "a@source-2#1")]
    [InlineData(7, ReviewFindingDirection.Next, "B@source-2#2")]
    [InlineData(7, ReviewFindingDirection.Previous, "M@source-4#3")]
    public void WorkspaceNavigationSearchesAtBoundaries(int logicalIndex, ReviewFindingDirection direction, string expectedId)
    {
        var workspace = CreateWorkspace();
        workspace.SelectFrame(logicalIndex);

        var occurrence = workspace.NavigateFinding(direction);

        Assert.Equal(expectedId, occurrence?.Id);
    }

    /// <summary>A selected occurrence uses its precise cached position within same-frame ties.</summary>
    [Fact]
    public void WorkspaceNavigationUsesSelectedOccurrencePosition()
    {
        var workspace = CreateWorkspace();
        workspace.SelectFinding("B", occurrenceIndex: 1);

        var next = workspace.NavigateFinding(ReviewFindingDirection.Next);
        var previous = workspace.NavigateFinding(ReviewFindingDirection.Previous);
        var earlier = workspace.NavigateFinding(ReviewFindingDirection.Previous);

        Assert.Equal("a@source-2#1", next?.Id);
        Assert.Equal("B@source-2#4", previous?.Id);
        Assert.Equal("B@source-2#2", earlier?.Id);
    }

    /// <summary>Filtering retains urgency order and clears selection after publishing its index.</summary>
    [Fact]
    public void WorkspaceFilterRetainsGroupOrderAndClearsHiddenSelection()
    {
        var workspace = CreateWorkspace();
        var originalGroups = workspace.VisibleGroups;
        workspace.SelectFinding("a");
        var navigationRevision = workspace.NavigationIndexRevision;
        var reportRevision = workspace.ReportRevision;

        workspace.SetFilter(ReviewQueueFilter.Warnings);

        Assert.Equal(["a", "B", "M", "A", "UNMAPPED"], originalGroups.Select(group => group.Id));
        Assert.Equal(["B", "M", "A", "UNMAPPED"], workspace.VisibleGroups.Select(group => group.Id));
        Assert.Null(workspace.SelectedFindingGroupId);
        Assert.Null(workspace.SelectedOccurrenceId);
        Assert.Equal(navigationRevision + 1, workspace.NavigationIndexRevision);
        Assert.Equal(reportRevision, workspace.ReportRevision);
        Assert.Throws<KeyNotFoundException>(() => workspace.SelectFinding("b"));
    }

    /// <summary>Visible occurrences without a logical index are excluded from navigation.</summary>
    [Fact]
    public void WorkspaceNavigationSkipsUnmappedOccurrences()
    {
        var workspace = CreateWorkspace();
        workspace.SelectFrame(2);
        var unmapped = workspace.SelectFinding("UNMAPPED");

        var next = workspace.NavigateFinding(ReviewFindingDirection.Next);

        Assert.Null(unmapped.LogicalIndex);
        Assert.Equal("M@source-4#3", next?.Id);
    }

    private static ReviewPresentationIndex CreateIndex()
    {
        ReviewOccurrence[] occurrences =
        [
            Occurrence("first", 2), Occurrence("second", 2), Occurrence("middle", 4), Occurrence("last", 7),
        ];
        var group = new ReviewEventGroup(
            "B", "B", "B", "B", ReviewEventCategory.ProtocolDiagnostic, DiagnosticSeverity.Warning,
            occurrences, CapturedAlarmState.NotApplicable, ReviewWorkflowState.Open, ReviewDisposition.None, null);
        return new ReviewPresentationIndex(
            [group], new Dictionary<string, ReviewEventGroup>(StringComparer.Ordinal) { ["B"] = group },
            [new(group, occurrences[0], 0), new(group, occurrences[1], 1), new(group, occurrences[2], 2), new(group, occurrences[3], 3)],
            new Dictionary<string, int>(StringComparer.Ordinal) { ["first"] = 0, ["second"] = 1, ["middle"] = 2, ["last"] = 3 });
    }

    private static ReviewOccurrence Occurrence(string id, int logicalIndex) => new(
        id, Diagnostic("B", $"source-{logicalIndex}"), ReviewEventCategory.ProtocolDiagnostic,
        CapturedAlarmState.NotApplicable, logicalIndex);

    private static ReviewInspectorWorkspace CreateWorkspace()
    {
        var frames = Enumerable.Range(0, 8).Select(Snapshot).ToArray();
        ReplayDiagnostic[] diagnostics =
        [
            Diagnostic("A", "source-7"),
            Diagnostic("a", "source-2", DiagnosticSeverity.Alarm),
            Diagnostic("B", "source-2"),
            Diagnostic("M", "source-4"),
            Diagnostic("B", "source-2"),
            Diagnostic("UNMAPPED", "unknown-source"),
        ];
        return new ReviewInspectorWorkspace(frames, diagnostics);
    }

    private static ITouchReplaySnapshot Snapshot(int logicalIndex)
    {
        var source = new SourceRecord(
            logicalIndex, $"source-{logicalIndex}", DateTimeOffset.UnixEpoch,
            BusOperation.Paint, "TP", 0x01, 0, [], $"frame {logicalIndex}", new SourceLocation(logicalIndex, logicalIndex + 1));
        var projection = new TouchReplayFrameProjection(source, [source], [], [], false, false, true);
        var time = TimeSpan.FromTicks(logicalIndex);
        var timeline = new ReplayTimelineEntry(logicalIndex, time, time, time, TimeSpan.Zero, false, false);
        return new TouchReplaySnapshot<object>(logicalIndex, new object(), projection, timeline, [], [], false, true);
    }

    private static ReplayDiagnostic Diagnostic(
        string code, string sourceId, DiagnosticSeverity severity = DiagnosticSeverity.Warning) =>
        new(severity, code, code, sourceId, new SourceLocation(0, 1));
}
