using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ReplayPaintWorkspaceTests
{
    [Fact]
    public void Defaults_match_the_current_Paint_contract()
    {
        var (replay, history) = Replay();

        var workspace = new ReplayPaintWorkspace(replay, history);

        Assert.Equal(new ReplayExtent(2048, 1280), workspace.Settings.PanelExtent);
        Assert.Equal(ReplayRenderMode.HostState, workspace.Settings.PointView);
        Assert.Equal(ReplayTrailMode.UntilBreak, workspace.Settings.TrailRetention);
        Assert.Equal(10, workspace.Settings.TrailLength);
        Assert.True(workspace.Settings.TraceVisible);
        Assert.True(workspace.Settings.TrailPointsVisible);
        Assert.Equal(ReplayLegendPosition.Auto, workspace.Settings.LegendPosition);
        Assert.Equal(ReplayLegendPosition.BottomRight, workspace.ResolvedLegendPosition);
    }

    [Fact]
    public void Empty_capture_extent_keeps_the_existing_default_measurement()
    {
        Assert.Equal(new ReplayExtent(2304, 1280), ReplayExtent.Measure([]));
    }

    [Fact]
    public void Equal_updates_do_not_change_revision_or_request_work()
    {
        var (replay, history) = Replay();
        var workspace = Workspace(replay, history);

        var update = workspace.Update(workspace.Settings with { });

        Assert.False(update.Changed);
        Assert.Equal(ReplayPaintChangeImpact.None, update.Impact);
        Assert.Equal(0, update.Revision);
        Assert.Equal(0, update.GeometryRevision);
    }

    [Fact]
    public void Settings_report_the_smallest_required_invalidation()
    {
        var (replay, history) = Replay();
        var workspace = Workspace(replay, history);

        var grid = workspace.Update(workspace.Settings with { StrongGrid = true });
        var extent = workspace.Update(workspace.Settings with { PanelExtent = new ReplayExtent(2560, 1280) });
        var reverse = workspace.Update(workspace.Settings with { ReverseX = true });
        var trail = workspace.Update(workspace.Settings with
        {
            TrailRetention = ReplayTrailMode.Recent,
            TrailLength = 2,
        });
        var swap = workspace.Update(workspace.Settings with { SwapAxes = true });
        var pointView = workspace.Update(workspace.Settings with { PointView = ReplayRenderMode.Compare });

        Assert.Equal(ReplayPaintChangeImpact.Surface, grid.Impact);
        Assert.True(grid.RequiresSurfaceUpdate);
        Assert.False(grid.RequiresSceneRebuild);
        Assert.Equal(
            ReplayPaintChangeImpact.Surface | ReplayPaintChangeImpact.Scene | ReplayPaintChangeImpact.Fit,
            extent.Impact);
        Assert.True(extent.RequiresSceneRebuild);
        Assert.True(extent.RequiresFit);
        Assert.All([reverse, trail], update => Assert.Equal(ReplayPaintChangeImpact.SceneOnly, update.Impact));
        Assert.Equal(ReplayPaintChangeImpact.GeometryCacheInvalidation, swap.Impact);
        Assert.True(swap.RequiresGeometryCacheInvalidation);
        Assert.False(swap.RequiresWorkspaceRebuild);
        Assert.Equal(ReplayPaintChangeImpact.Surface | ReplayPaintChangeImpact.Inspector, pointView.Impact);
        Assert.True(pointView.RequiresInspectorRefresh);
        Assert.False(pointView.RequiresSceneRebuild);
        Assert.Equal(6, workspace.Revision);
        Assert.Equal(1, workspace.GeometryRevision);
    }

    [Fact]
    public void Host_reported_and_compare_point_views_map_to_renderer_layers()
    {
        var (replay, history) = Replay();
        var workspace = Workspace(replay, history);

        Assert.Equal(ReplayRenderMode.HostState, workspace.CreateRenderSettings().Mode);

        workspace.Update(workspace.Settings with { PointView = ReplayRenderMode.ReportedFrame });
        Assert.Equal(ReplayRenderMode.ReportedFrame, workspace.CreateRenderSettings().Mode);

        workspace.Update(workspace.Settings with { PointView = ReplayRenderMode.Compare });
        Assert.Equal(ReplayRenderMode.Compare, workspace.CreateRenderSettings().Mode);

        var scene = workspace.CreateScene(1);
        Assert.Equal((120, 220), (scene.ReportedContacts[0].X, scene.ReportedContacts[0].Y));
        Assert.Equal((115, 215), (scene.HostContacts[0].X, scene.HostContacts[0].Y));
    }

    [Fact]
    public void Axis_transform_is_carried_by_the_scene_without_changing_source_coordinates()
    {
        var (replay, history) = Replay();
        var settings = Settings() with
        {
            PanelExtent = new ReplayExtent(2000, 1000),
            ReverseX = true,
            ReverseY = true,
            SwapAxes = true,
        };
        var workspace = new ReplayPaintWorkspace(replay, history, settings, diagnostics: []);

        var scene = workspace.CreateScene(1);

        Assert.True(scene.ReverseX);
        Assert.True(scene.ReverseY);
        Assert.True(scene.SwapAxes);
        Assert.Equal(new ReplayExtent(1000, 2000), scene.ViewExtent);
        Assert.Equal((220d, 120d), scene.ViewCoordinates(120, 220));
        Assert.Equal((120, 220), (scene.ReportedContacts[0].X, scene.ReportedContacts[0].Y));
    }

    [Fact]
    public void Trace_lines_and_report_points_are_independent_without_discarding_history()
    {
        var (replay, history) = Replay();
        var workspace = Workspace(replay, history);

        workspace.Update(workspace.Settings with { TraceVisible = false, TrailPointsVisible = true });
        var pointsOnly = workspace.CreateScene(1);
        Assert.False(workspace.CreateRenderSettings().ShowTrailLines);
        Assert.True(workspace.CreateRenderSettings().ShowTrailPoints);
        Assert.Equal(2, Assert.Single(pointsOnly.ContactTrails).Points.Count);

        workspace.Update(workspace.Settings with { TraceVisible = true, TrailPointsVisible = false });
        var lineOnly = workspace.CreateScene(1);
        Assert.True(workspace.CreateRenderSettings().ShowTrailLines);
        Assert.False(workspace.CreateRenderSettings().ShowTrailPoints);
        Assert.Equal(2, Assert.Single(lineOnly.ContactTrails).Points.Count);

        workspace.Update(workspace.Settings with { TraceVisible = false, TrailPointsVisible = false });
        Assert.Empty(workspace.CreateScene(1).ContactTrails);
        Assert.Same(history, workspace.TrailHistory);
    }

    [Fact]
    public void Recent_retention_and_clear_boundary_are_scene_state_not_workspace_rebuilds()
    {
        var (replay, history) = Replay();
        var workspace = Workspace(replay, history);

        var recent = workspace.Update(workspace.Settings with
        {
            TrailRetention = ReplayTrailMode.Recent,
            TrailLength = 2,
        });
        Assert.Equal([0, 1], Assert.Single(workspace.CreateScene(1).ContactTrails).Points.Select(point => point.LogicalIndex));

        var clear = workspace.Update(workspace.Settings with { TrailVisibilityStart = 2 });
        Assert.Empty(workspace.CreateScene(1).ContactTrails);
        Assert.Equal(ReplayPaintChangeImpact.SceneOnly, recent.Impact);
        Assert.Equal(ReplayPaintChangeImpact.SceneOnly, clear.Impact);
        Assert.False(clear.RequiresWorkspaceRebuild);
    }

    [Fact]
    public void Creating_render_settings_keeps_replay_and_history_by_reference_and_does_not_seek_source_data()
    {
        var (replay, history) = Replay();
        var workspace = Workspace(replay, history);
        var seekCount = replay.SeekCount;
        var reportedContactsReadCount = replay.AllReportedContactsReadCount;

        var renderSettings = workspace.CreateRenderSettings(showFrameChrome: true);

        Assert.Same(replay, workspace.Replay);
        Assert.Same(history, workspace.TrailHistory);
        Assert.Equal(seekCount, replay.SeekCount);
        Assert.Equal(reportedContactsReadCount, replay.AllReportedContactsReadCount);
        Assert.True(renderSettings.ShowFrameChrome);
    }

    [Fact]
    public void Annotation_refresh_updates_scene_without_rebuilding_replay_history_or_settings()
    {
        var (replay, history) = Replay();
        var settings = Settings();
        var createdAt = DateTimeOffset.UnixEpoch;
        var diagnostics = new List<ReplayDiagnostic>
        {
            Diagnostic("INITIAL_WARNING", DiagnosticSeverity.Warning),
        };
        var markers = new List<ReplayMarker>
        {
            Marker("marker-1", "Initial marker", createdAt),
        };
        var workspace = new ReplayPaintWorkspace(replay, history, settings, diagnostics, markers);
        var snapshot = replay.Seek(0);

        diagnostics[0] = Diagnostic("UPDATED_ALARM", DiagnosticSeverity.Alarm);
        markers[0] = markers[0] with { Label = "Renamed marker" };
        var beforeRefresh = workspace.CreateScene(snapshot);
        var update = workspace.UpdateAnnotations(diagnostics, markers);
        var afterRefresh = workspace.CreateScene(snapshot);

        Assert.Equal("INITIAL_WARNING", Assert.Single(beforeRefresh.Diagnostics).Code);
        Assert.Equal("Initial marker", Assert.Single(beforeRefresh.MarkerLabels));
        Assert.True(update.Changed);
        Assert.Equal(1, update.Revision);
        Assert.Equal(1, workspace.AnnotationRevision);
        Assert.Equal("UPDATED_ALARM", Assert.Single(afterRefresh.Diagnostics).Code);
        Assert.Equal("Renamed marker", Assert.Single(afterRefresh.MarkerLabels));
        Assert.Same(replay, workspace.Replay);
        Assert.Same(history, workspace.TrailHistory);
        Assert.Same(settings, workspace.Settings);
        Assert.Equal(0, workspace.Revision);
        Assert.Equal(0, workspace.GeometryRevision);
    }

    [Fact]
    public void Equivalent_annotation_refresh_is_a_no_op_and_external_lists_cannot_mutate_the_snapshot()
    {
        var (replay, history) = Replay();
        var createdAt = DateTimeOffset.UnixEpoch;
        var diagnostics = new List<ReplayDiagnostic>
        {
            Diagnostic("WARNING", DiagnosticSeverity.Warning, new Dictionary<string, string> { ["state"] = "open" }),
        };
        var markers = new List<ReplayMarker>
        {
            Marker("marker-1", "Marker", createdAt, ["QA-1"]),
        };
        var workspace = new ReplayPaintWorkspace(replay, history, Settings(), diagnostics, markers);

        diagnostics.Clear();
        markers.Clear();
        var initialScene = workspace.CreateScene(replay.Seek(0));
        var update = workspace.UpdateAnnotations(
            [Diagnostic("WARNING", DiagnosticSeverity.Warning, new Dictionary<string, string> { ["state"] = "open" })],
            [Marker("marker-1", "Marker", createdAt, ["QA-1"])]);

        Assert.Single(initialScene.Diagnostics);
        Assert.Single(initialScene.MarkerLabels);
        Assert.False(update.Changed);
        Assert.Equal(0, update.Revision);
        Assert.Equal(0, workspace.AnnotationRevision);
    }

    [Fact]
    public void Marker_add_rename_and_remove_each_publish_the_current_scene_annotations()
    {
        var (replay, history) = Replay();
        var workspace = new ReplayPaintWorkspace(replay, history, Settings(), diagnostics: [], markers: []);
        var snapshot = replay.Seek(0);
        var marker = Marker("marker-1", "Marker 1", DateTimeOffset.UnixEpoch);

        var added = workspace.UpdateAnnotations(
            [Diagnostic("ANNOTATION_MARKER", DiagnosticSeverity.Info)],
            [marker]);
        var addedScene = workspace.CreateScene(snapshot);
        var renamed = workspace.UpdateAnnotations(
            [Diagnostic("ANNOTATION_MARKER", DiagnosticSeverity.Info)],
            [marker with { Label = "Baseline reset" }]);
        var renamedScene = workspace.CreateScene(snapshot);
        var removed = workspace.UpdateAnnotations([], []);
        var removedScene = workspace.CreateScene(snapshot);

        Assert.Equal((1, true), (added.Revision, added.Changed));
        Assert.Equal("Marker 1", Assert.Single(addedScene.MarkerLabels));
        Assert.Equal((2, true), (renamed.Revision, renamed.Changed));
        Assert.Equal("Baseline reset", Assert.Single(renamedScene.MarkerLabels));
        Assert.Equal((3, true), (removed.Revision, removed.Changed));
        Assert.Empty(removedScene.Diagnostics);
        Assert.Empty(removedScene.MarkerLabels);
        Assert.Same(history, workspace.TrailHistory);
    }

    [Fact]
    public void Annotation_index_preserves_global_diagnostic_order_deduplicates_source_ids_and_projects_marker_ranges()
    {
        var sourceA = Source(0, "source-a");
        var sourceB = Source(1, "source-b");
        var snapshot = new MultiSourceSnapshot(
            0,
            sourceA,
            [sourceB, sourceA, sourceB],
            [new ReplayContact(1, TouchType.Finger, TouchStatus.Move, 10, 20, sourceA.StableId)]);
        var replay = new FakeReplay([snapshot]);
        var history = ReplayTrailHistory.Create(new ITouchReplaySnapshot[] { snapshot });
        var diagnostics = new[]
        {
            DiagnosticFor("A1", sourceA.StableId),
            DiagnosticFor("B1", sourceB.StableId),
            DiagnosticFor("A2", sourceA.StableId),
            DiagnosticFor("B2", sourceB.StableId),
            DiagnosticFor("OTHER", "source-other"),
        };
        var markers = new[]
        {
            new ReplayMarker("range", "Range marker", -10, 10, DateTimeOffset.UnixEpoch),
            new ReplayMarker("outside", "Outside", 2, 4, DateTimeOffset.UnixEpoch),
        };
        var workspace = new ReplayPaintWorkspace(replay, history, Settings(), diagnostics, markers);

        var scene = workspace.CreateScene(0);

        Assert.Equal(["A1", "B1", "A2", "B2"], scene.Diagnostics.Select(item => item.Code));
        Assert.Equal(["Range marker"], scene.MarkerLabels);
    }

    [Fact]
    public void Repeated_scene_creation_reuses_single_source_annotation_projection_with_bounded_allocations()
    {
        const int diagnosticCount = 20_000;
        const int repetitions = 250;
        var (replay, history) = Replay();
        var diagnostics = Enumerable.Range(0, diagnosticCount)
            .Select(index => DiagnosticFor($"D{index}", "source-0"))
            .ToArray();
        var workspace = new ReplayPaintWorkspace(
            replay,
            history,
            Settings() with { TraceVisible = false, TrailPointsVisible = false },
            diagnostics,
            [Marker("marker", "Marker", DateTimeOffset.UnixEpoch)]);
        var first = workspace.CreateScene(0);
        var second = workspace.CreateScene(0);

        Assert.Same(first.Diagnostics, second.Diagnostics);
        Assert.Same(first.MarkerLabels, second.MarkerLabels);
        Assert.Equal(diagnosticCount, first.Diagnostics.Count);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < repetitions; index++)
            _ = workspace.CreateScene(0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 1_000_000);
    }

    [Fact]
    public void Hundred_thousand_contact_legend_updates_use_the_compact_summary_without_reenumerating_contacts()
    {
        const int contactCount = 100_000;
        var contacts = Enumerable.Range(0, contactCount)
            .Select(index => new ReplayContact(
                (byte)((index % 10) + 1),
                TouchType.Finger,
                index == 0 ? TouchStatus.Enter : TouchStatus.Move,
                (ushort)(120 + (index % 700)),
                (ushort)(80 + ((index * 7) % 500)),
                "source-0"))
            .ToArray();
        var countedContacts = new CountingReadOnlyList<ReplayContact>(contacts);
        var snapshot = new MultiSourceSnapshot(0, Source(0, "source-0"), [], contacts);
        var replay = new FakeReplay([snapshot], countedContacts);
        var history = ReplayTrailHistory.Create(new ITouchReplaySnapshot[] { snapshot });
        var settings = ReplayPaintSettings.Default(new ReplayExtent(4096, 2048)) with
        {
            TraceVisible = false,
            TrailPointsVisible = false,
        };
        var workspace = new ReplayPaintWorkspace(replay, history, settings, diagnostics: []);
        var enumerationsAfterBuild = countedContacts.EnumerationCount;

        foreach (var extent in new[] { new ReplayExtent(4096, 2048), new ReplayExtent(4608, 2304) })
        foreach (var reverseX in new[] { false, true })
        foreach (var reverseY in new[] { false, true })
        foreach (var swapAxes in new[] { false, true })
        {
            workspace.Update(workspace.Settings with
            {
                PanelExtent = extent,
                ReverseX = reverseX,
                ReverseY = reverseY,
                SwapAxes = swapAxes,
            });
            Assert.Equal(
                ReplayLegendPositioner.Choose(contacts, extent, reverseX, reverseY, swapAxes),
                workspace.ResolvedLegendPosition);
        }

        for (var index = 0; index < 250; index++)
        {
            var next = workspace.Settings with
            {
                PanelExtent = index % 2 == 0 ? new ReplayExtent(4096, 2048) : new ReplayExtent(4608, 2304),
                ReverseX = (index & 1) != 0,
                ReverseY = (index & 2) != 0,
                SwapAxes = (index & 4) != 0,
            };
            workspace.Update(next);
        }

        Assert.Equal(enumerationsAfterBuild, countedContacts.EnumerationCount);
        Assert.InRange(enumerationsAfterBuild, 1, 2);
    }

    [Fact]
    public void Rejects_invalid_settings_and_trail_history_from_another_replay()
    {
        var (replay, history) = Replay();
        var workspace = Workspace(replay, history);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workspace.Update(workspace.Settings with { TrailLength = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            workspace.Update(workspace.Settings with { PanelExtent = new ReplayExtent(double.NaN, 1280) }));
        Assert.Throws<ArgumentException>(() =>
            new ReplayPaintWorkspace(replay, ReplayTrailHistory.Create(Array.Empty<ITouchReplaySnapshot>()), Settings()));
    }

    private static ReplayPaintWorkspace Workspace(FakeReplay replay, ReplayTrailHistory history) =>
        new(replay, history, Settings(), diagnostics: []);

    private static ReplayPaintSettings Settings() =>
        ReplayPaintSettings.Default(new ReplayExtent(1920, 1080)) with
        {
            LegendPosition = ReplayLegendPosition.TopLeft,
        };

    private static ReplayDiagnostic Diagnostic(
        string code,
        DiagnosticSeverity severity,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(severity, code, code, "source-0", new SourceLocation(0, 1), details);

    private static ReplayDiagnostic DiagnosticFor(string code, string sourceId) =>
        new(DiagnosticSeverity.Warning, code, code, sourceId, new SourceLocation(0, 1));

    private static ReplayMarker Marker(
        string id,
        string label,
        DateTimeOffset createdAt,
        IReadOnlyList<string>? qaCaseIds = null) =>
        new(id, label, 0, 0, createdAt, QaCaseIds: qaCaseIds);

    private static (FakeReplay Replay, ReplayTrailHistory History) Replay()
    {
        var snapshots = new ITouchReplaySnapshot[]
        {
            Snapshot(0, Contact(0, TouchStatus.Enter, 100, 200), Contact(0, TouchStatus.Enter, 100, 200)),
            Snapshot(1, Contact(1, TouchStatus.Move, 120, 220), Contact(1, TouchStatus.Move, 115, 215)),
            Snapshot(2, Contact(2, TouchStatus.Break, 120, 220), host: null),
            Snapshot(3, new ReplayContact(2, TouchType.Glove, TouchStatus.Enter, 900, 400, "source-3"),
                new ReplayContact(2, TouchType.Glove, TouchStatus.Enter, 900, 400, "source-3")),
        };
        return (new FakeReplay(snapshots), ReplayTrailHistory.Create(snapshots));
    }

    private static ReplayContact Contact(int index, TouchStatus status, ushort x, ushort y) =>
        new(1, TouchType.Finger, status, x, y, $"source-{index}");

    private static SourceRecord Source(int index, string stableId) =>
        new(
            index,
            stableId,
            DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(index * 10),
            BusOperation.Paint,
            "TP",
            1,
            80,
            [],
            string.Empty,
            new SourceLocation(index * 10, index + 1));

    private static FakeSnapshot Snapshot(
        int index,
        ReplayContact reported,
        ReplayContact? host)
    {
        var source = new SourceRecord(
            index,
            $"source-{index}",
            DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(index * 10),
            BusOperation.Paint,
            "TP",
            1,
            80,
            [],
            string.Empty,
            new SourceLocation(index * 10, index + 1));
        var time = TimeSpan.FromMilliseconds(index * 10);
        return new FakeSnapshot(
            index,
            source,
            new ReplayTimelineEntry(
                index,
                time,
                time,
                time,
                index == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(10),
                false,
                false),
            [reported],
            host is null ? [] : [host]);
    }

    private sealed record FakeSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        ReplayTimelineEntry Timeline,
        IReadOnlyList<ReplayContact> ReportedContacts,
        IReadOnlyList<ReplayContact> HostContacts) : ITouchReplaySnapshot
    {
        public object DecodedFrame => this;
        public IReadOnlyList<SourceRecord> PhysicalRecords => [PrimarySource];
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }

    private sealed class FakeReplay : ITouchReplaySession
    {
        private readonly IReadOnlyList<ITouchReplaySnapshot> snapshots;
        private readonly IReadOnlyList<ReplayContact> allReportedContacts;

        public FakeReplay(
            IReadOnlyList<ITouchReplaySnapshot> snapshots,
            IReadOnlyList<ReplayContact>? allReportedContacts = null)
        {
            this.snapshots = snapshots;
            this.allReportedContacts = allReportedContacts ?? snapshots.SelectMany(snapshot => snapshot.ReportedContacts).ToArray();
            Timeline = snapshots.Select(snapshot => snapshot.Timeline).ToArray();
        }

        public int Count => snapshots.Count;
        public int SeekCount { get; private set; }
        public int AllReportedContactsReadCount { get; private set; }
        public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(10);
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; }
        public IReadOnlyList<ReplayDiagnostic> Diagnostics => [];
        public IReadOnlyList<ReplayContact> AllReportedContacts
        {
            get
            {
                AllReportedContactsReadCount++;
                return allReportedContacts;
            }
        }

        public ITouchReplaySnapshot Seek(int logicalIndex)
        {
            SeekCount++;
            return snapshots[logicalIndex];
        }
    }

    private sealed record MultiSourceSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        IReadOnlyList<SourceRecord> PhysicalRecords,
        IReadOnlyList<ReplayContact> ReportedContacts) : ITouchReplaySnapshot
    {
        public object DecodedFrame => this;
        public ReplayTimelineEntry Timeline { get; } = new(
            LogicalIndex,
            TimeSpan.FromMilliseconds(LogicalIndex),
            TimeSpan.FromMilliseconds(LogicalIndex),
            TimeSpan.FromMilliseconds(LogicalIndex),
            TimeSpan.Zero,
            false,
            false);
        public IReadOnlyList<ReplayContact> HostContacts => ReportedContacts;
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }

    private sealed class CountingReadOnlyList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        public int EnumerationCount { get; private set; }
        public int Count => values.Count;
        public T this[int index] => values[index];

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationCount++;
            return values.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
