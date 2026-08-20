using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public enum ReplayPaintChangeImpact
{
    None,
    SceneOnly,
    GeometryCacheInvalidation,
    WorkspaceRebuild,
}

public readonly record struct ReplayPaintUpdate(
    long Revision,
    long GeometryRevision,
    ReplayPaintChangeImpact Impact)
{
    public bool Changed => Impact != ReplayPaintChangeImpact.None;
    public bool RequiresGeometryCacheInvalidation =>
        Impact is ReplayPaintChangeImpact.GeometryCacheInvalidation or ReplayPaintChangeImpact.WorkspaceRebuild;
    public bool RequiresWorkspaceRebuild => Impact == ReplayPaintChangeImpact.WorkspaceRebuild;
}

public readonly record struct ReplayPaintAnnotationUpdate(
    long Revision,
    bool Changed);

public sealed record ReplayPaintSettings(
    ReplayExtent PanelExtent,
    ReplayRenderMode PointView,
    ReplayTrailMode TrailRetention,
    int TrailLength,
    int TrailVisibilityStart,
    bool TraceVisible,
    bool TrailPointsVisible,
    bool ReverseX,
    bool ReverseY,
    bool SwapAxes,
    bool StrongGrid,
    bool LegendVisible,
    bool LegendCollapsed,
    ReplayLegendPosition LegendPosition,
    ReplayRenderTheme Theme)
{
    public static ReplayPaintSettings Default(ReplayExtent panelExtent) => new(
        panelExtent,
        ReplayRenderMode.HostState,
        ReplayTrailMode.UntilBreak,
        10,
        0,
        TraceVisible: true,
        TrailPointsVisible: true,
        ReverseX: false,
        ReverseY: false,
        SwapAxes: false,
        StrongGrid: false,
        LegendVisible: true,
        LegendCollapsed: true,
        ReplayLegendPosition.Auto,
        ReplayRenderTheme.Dark);
}

/// <summary>
/// Headless Paint state over immutable replay and trail-history references. Paint setting changes
/// never rebuild decoded source data. The returned impact lets a presentation layer redraw the
/// current scene or invalidate transformed geometry only when required.
/// </summary>
public sealed class ReplayPaintWorkspace
{
    public const double MaximumPanelCoordinate = 1_000_000;

    private readonly ITouchReplaySession replay;
    private readonly ReplayTrailHistory trailHistory;
    private IReadOnlyList<ReplayDiagnostic> diagnostics;
    private IReadOnlyList<ReplayMarker> markers;

    public ReplayPaintWorkspace(
        ITouchReplaySession replay,
        ReplayTrailHistory trailHistory,
        ReplayPaintSettings? settings = null,
        IReadOnlyList<ReplayDiagnostic>? diagnostics = null,
        IReadOnlyList<ReplayMarker>? markers = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(trailHistory);
        if (trailHistory.Count != replay.Count)
            throw new ArgumentException("Trail history must describe the same number of frames as the replay.", nameof(trailHistory));

        this.replay = replay;
        this.trailHistory = trailHistory;
        this.diagnostics = Snapshot(diagnostics ?? replay.Diagnostics);
        this.markers = Snapshot(markers ?? []);
        Settings = settings ?? ReplayPaintSettings.Default(ReplayExtent.Measure(replay.AllReportedContacts));
        Validate(Settings, replay.Count);
        ResolvedLegendPosition = ResolveLegendPosition(Settings);
    }

    public ITouchReplaySession Replay => replay;
    public ReplayTrailHistory TrailHistory => trailHistory;
    public IReadOnlyList<ReplayDiagnostic> Diagnostics => diagnostics;
    public IReadOnlyList<ReplayMarker> Markers => markers;
    public ReplayPaintSettings Settings { get; private set; }
    public ReplayLegendPosition ResolvedLegendPosition { get; private set; }
    public long Revision { get; private set; }
    public long GeometryRevision { get; private set; }
    public long AnnotationRevision { get; private set; }

    public ReplayPaintUpdate Update(ReplayPaintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings, replay.Count);
        if (settings == Settings)
            return new ReplayPaintUpdate(Revision, GeometryRevision, ReplayPaintChangeImpact.None);

        var previous = Settings;
        var impact = Classify(previous, settings);
        var nextRevision = checked(Revision + 1);
        var nextGeometryRevision = impact == ReplayPaintChangeImpact.GeometryCacheInvalidation
            ? checked(GeometryRevision + 1)
            : GeometryRevision;
        var nextLegendPosition = LegendPlacementChanged(previous, settings)
            ? ResolveLegendPosition(settings)
            : ResolvedLegendPosition;
        Settings = settings;
        Revision = nextRevision;
        GeometryRevision = nextGeometryRevision;
        ResolvedLegendPosition = nextLegendPosition;
        return new ReplayPaintUpdate(Revision, GeometryRevision, impact);
    }

    public ReplayPaintAnnotationUpdate UpdateAnnotations(
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyList<ReplayMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(markers);
        if (DiagnosticsEqual(this.diagnostics, diagnostics) && MarkersEqual(this.markers, markers))
            return new ReplayPaintAnnotationUpdate(AnnotationRevision, Changed: false);

        this.diagnostics = Snapshot(diagnostics);
        this.markers = Snapshot(markers);
        AnnotationRevision = checked(AnnotationRevision + 1);
        return new ReplayPaintAnnotationUpdate(AnnotationRevision, Changed: true);
    }

    public ReplayScene CreateScene(int logicalIndex) => CreateScene(replay.Seek(logicalIndex));

    public ReplayScene CreateScene(ITouchReplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.LogicalIndex < 0 || snapshot.LogicalIndex >= replay.Count)
            throw new ArgumentOutOfRangeException(nameof(snapshot), "Snapshot must belong to the replay frame range.");

        var trails = Settings.TraceVisible || Settings.TrailPointsVisible
            ? trailHistory.Build(
                snapshot.LogicalIndex,
                Settings.TrailRetention,
                Settings.TrailLength,
                Settings.TrailVisibilityStart)
            : [];
        return ReplaySceneFactory.Create(
            snapshot,
            replay.Count,
            Settings.PanelExtent,
            diagnostics,
            markers,
            trails,
            Settings.ReverseX,
            Settings.ReverseY,
            Settings.SwapAxes);
    }

    public ReplayRenderSettings CreateRenderSettings(bool showFrameChrome = false) => new(
        Settings.PointView,
        Settings.Theme,
        Settings.StrongGrid,
        Settings.LegendVisible,
        Settings.LegendCollapsed,
        ResolvedLegendPosition,
        showFrameChrome,
        Settings.TraceVisible,
        Settings.TrailPointsVisible);

    private ReplayLegendPosition ResolveLegendPosition(ReplayPaintSettings settings) =>
        settings.LegendPosition == ReplayLegendPosition.Auto
            ? ReplayLegendPositioner.Choose(
                replay.AllReportedContacts,
                settings.PanelExtent,
                settings.ReverseX,
                settings.ReverseY,
                settings.SwapAxes)
            : settings.LegendPosition;

    private static ReplayPaintChangeImpact Classify(
        ReplayPaintSettings previous,
        ReplayPaintSettings next) =>
        previous.SwapAxes != next.SwapAxes
            ? ReplayPaintChangeImpact.GeometryCacheInvalidation
            : ReplayPaintChangeImpact.SceneOnly;

    private static bool LegendPlacementChanged(
        ReplayPaintSettings previous,
        ReplayPaintSettings next) =>
        previous.LegendPosition != next.LegendPosition ||
        previous.PanelExtent != next.PanelExtent ||
        previous.ReverseX != next.ReverseX ||
        previous.ReverseY != next.ReverseY ||
        previous.SwapAxes != next.SwapAxes;

    private static void Validate(ReplayPaintSettings settings, int replayCount)
    {
        ArgumentNullException.ThrowIfNull(settings.PanelExtent);
        if (!double.IsFinite(settings.PanelExtent.MaximumX) ||
            !double.IsFinite(settings.PanelExtent.MaximumY) ||
            settings.PanelExtent.MaximumX is < 1 or > MaximumPanelCoordinate ||
            settings.PanelExtent.MaximumY is < 1 or > MaximumPanelCoordinate)
            throw new ArgumentOutOfRangeException(nameof(settings), $"Panel coordinates must be finite values from 1 to {MaximumPanelCoordinate:N0}.");
        if (settings.TrailLength is < 2 or > 120)
            throw new ArgumentOutOfRangeException(nameof(settings), "Trail length must be from 2 to 120 points.");
        if (settings.TrailVisibilityStart < 0 || settings.TrailVisibilityStart > replayCount)
            throw new ArgumentOutOfRangeException(nameof(settings), "Trail visibility start must be within the replay range.");
        if (!Enum.IsDefined(settings.PointView) ||
            !Enum.IsDefined(settings.TrailRetention) ||
            !Enum.IsDefined(settings.LegendPosition) ||
            !Enum.IsDefined(settings.Theme))
            throw new ArgumentOutOfRangeException(nameof(settings), "Paint settings contain an unknown enum value.");
    }

    private static IReadOnlyList<T> Snapshot<T>(IReadOnlyList<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static bool DiagnosticsEqual(
        IReadOnlyList<ReplayDiagnostic> left,
        IReadOnlyList<ReplayDiagnostic> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first.Severity != second.Severity ||
                first.Code != second.Code ||
                first.Message != second.Message ||
                first.SourceRecordId != second.SourceRecordId ||
                first.Location != second.Location ||
                !DetailsEqual(first.Details, second.Details))
                return false;
        }
        return true;
    }

    private static bool DetailsEqual(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (var (key, value) in left)
            if (!right.TryGetValue(key, out var candidate) || candidate != value) return false;
        return true;
    }

    private static bool MarkersEqual(
        IReadOnlyList<ReplayMarker> left,
        IReadOnlyList<ReplayMarker> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (!MarkerEqual(left[index], right[index])) return false;
        return true;
    }

    private static bool MarkerEqual(ReplayMarker left, ReplayMarker right) =>
        left.Id == right.Id &&
        left.Label == right.Label &&
        left.StartLogicalIndex == right.StartLogicalIndex &&
        left.EndLogicalIndex == right.EndLogicalIndex &&
        left.CreatedAt == right.CreatedAt &&
        left.Note == right.Note &&
        (left.QaCaseIds ?? []).SequenceEqual(right.QaCaseIds ?? [], StringComparer.Ordinal) &&
        (left.Evidence ?? []).SequenceEqual(right.Evidence ?? []);
}
