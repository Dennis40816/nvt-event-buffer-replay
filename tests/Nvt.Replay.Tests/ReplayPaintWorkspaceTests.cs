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

        Assert.All([grid, extent, reverse, trail], update =>
        {
            Assert.Equal(ReplayPaintChangeImpact.SceneOnly, update.Impact);
            Assert.False(update.RequiresGeometryCacheInvalidation);
            Assert.False(update.RequiresWorkspaceRebuild);
        });
        Assert.Equal(ReplayPaintChangeImpact.GeometryCacheInvalidation, swap.Impact);
        Assert.True(swap.RequiresGeometryCacheInvalidation);
        Assert.False(swap.RequiresWorkspaceRebuild);
        Assert.Equal(5, workspace.Revision);
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

    private sealed class FakeReplay(IReadOnlyList<ITouchReplaySnapshot> snapshots) : ITouchReplaySession
    {
        private readonly IReadOnlyList<ReplayContact> allReportedContacts =
            snapshots.SelectMany(snapshot => snapshot.ReportedContacts).ToArray();

        public int Count => snapshots.Count;
        public int SeekCount { get; private set; }
        public int AllReportedContactsReadCount { get; private set; }
        public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(10);
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; } =
            snapshots.Select(snapshot => snapshot.Timeline).ToArray();
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
}
