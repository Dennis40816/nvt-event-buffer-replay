using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

/// <summary>Specifies the rendering, geometry, inspector, and workspace work required by a Paint change.</summary>
[Flags]
public enum ReplayPaintChangeImpact
{
    /// <summary>Indicates that no Paint update work is required.</summary>
    None = 0,
    /// <summary>Requires an update to the rendered Paint surface.</summary>
    Surface = 1 << 0,
    /// <summary>Requires rebuilding the projected Paint scene.</summary>
    Scene = 1 << 1,
    /// <summary>Requires invalidating cached transformed geometry.</summary>
    Geometry = 1 << 2,
    /// <summary>Requires fitting the Paint view to the panel extent.</summary>
    Fit = 1 << 3,
    /// <summary>Requires refreshing the Paint inspector.</summary>
    Inspector = 1 << 4,
    /// <summary>Requires rebuilding the Paint workspace.</summary>
    Workspace = 1 << 5,

    // Named compound values keep the public 0.0.x contract readable while allowing the UI to
    // consume orthogonal work requirements instead of maintaining parallel boolean guesses.
    /// <summary>Requires a surface update and scene rebuild.</summary>
    SceneOnly = Surface | Scene,
    /// <summary>Requires a surface update, scene rebuild, and geometry cache invalidation.</summary>
    GeometryCacheInvalidation = Surface | Scene | Geometry,
    /// <summary>Requires surface, scene, geometry, fit, and workspace updates.</summary>
    WorkspaceRebuild = Surface | Scene | Geometry | Fit | Workspace,
}

/// <summary>Reports Paint setting revisions and the work required to apply a settings update.</summary>
/// <param name="Revision">The settings revision after the update.</param>
/// <param name="GeometryRevision">The geometry revision after the update.</param>
/// <param name="Impact">The rendering and workspace work required by the update.</param>
public readonly record struct ReplayPaintUpdate(
    long Revision,
    long GeometryRevision,
    ReplayPaintChangeImpact Impact)
{
    /// <summary>Gets whether the update reports any required Paint work.</summary>
    public bool Changed => Impact != ReplayPaintChangeImpact.None;
    /// <summary>Gets whether the impact flags require a Paint surface update.</summary>
    public bool RequiresSurfaceUpdate => (Impact & ReplayPaintChangeImpact.Surface) != 0;
    /// <summary>Gets whether the impact flags require rebuilding the Paint scene.</summary>
    public bool RequiresSceneRebuild => (Impact & ReplayPaintChangeImpact.Scene) != 0;
    /// <summary>Gets whether the impact flags require invalidating cached geometry.</summary>
    public bool RequiresGeometryCacheInvalidation =>
        (Impact & ReplayPaintChangeImpact.Geometry) != 0;
    /// <summary>Gets whether the impact flags require fitting the Paint view.</summary>
    public bool RequiresFit => (Impact & ReplayPaintChangeImpact.Fit) != 0;
    /// <summary>Gets whether the impact flags require refreshing the Paint inspector.</summary>
    public bool RequiresInspectorRefresh => (Impact & ReplayPaintChangeImpact.Inspector) != 0;
    /// <summary>Gets whether the impact flags require rebuilding the Paint workspace.</summary>
    public bool RequiresWorkspaceRebuild => (Impact & ReplayPaintChangeImpact.Workspace) != 0;
}

/// <summary>Reports the annotation revision and whether diagnostics or markers changed.</summary>
/// <param name="Revision">The annotation revision after the update.</param>
/// <param name="Changed">Whether the diagnostics or markers differed from the previous annotation state.</param>
public readonly record struct ReplayPaintAnnotationUpdate(
    long Revision,
    bool Changed);

/// <summary>Stores panel, trail, coordinate transform, legend, and theme settings for Paint rendering.</summary>
/// <param name="PanelExtent">The panel coordinate extent used for projection and legend placement.</param>
/// <param name="PointView">The point display mode passed to the renderer.</param>
/// <param name="TrailRetention">The retention mode used to construct trail history.</param>
/// <param name="TrailLength">The requested trail length, validated to range from 2 through 120.</param>
/// <param name="TrailVisibilityStart">The logical frame index at which visible trail history starts.</param>
/// <param name="TraceVisible">Whether trail traces are displayed.</param>
/// <param name="TrailPointsVisible">Whether individual trail points are displayed.</param>
/// <param name="ReverseX">Whether X reversal is enabled for projection and legend placement.</param>
/// <param name="ReverseY">Whether Y reversal is enabled for projection and legend placement.</param>
/// <param name="SwapAxes">Whether X and Y axes are swapped for projection and legend placement.</param>
/// <param name="StrongGrid">Whether the renderer uses the strong grid setting.</param>
/// <param name="LegendVisible">Whether the legend is displayed.</param>
/// <param name="LegendCollapsed">Whether the legend is displayed in its collapsed form.</param>
/// <param name="LegendPosition">The requested legend position, which may be selected automatically.</param>
/// <param name="Theme">The visual theme passed to the renderer.</param>
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
    /// <summary>Creates host-state Paint settings with trails enabled, automatic legend placement, and the dark theme.</summary>
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
    /// <summary>Defines the largest allowed X or Y maximum in a Paint panel extent.</summary>
    public const double MaximumPanelCoordinate = 1_000_000;

    private readonly ITouchReplaySession replay;
    private readonly ReplayTrailHistory trailHistory;
    private readonly ReplayLegendOccupancySummary legendOccupancy;
    private IReadOnlyList<ReplayDiagnostic> diagnostics;
    private IReadOnlyList<ReplayMarker> markers;
    private ReplayPaintAnnotationIndex annotationIndex;

    /// <summary>Creates Paint state for a replay and matching trail history, with validated settings and annotation snapshots.</summary>
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
        var allReportedContacts = replay.AllReportedContacts;
        this.diagnostics = Snapshot(diagnostics ?? replay.Diagnostics);
        this.markers = Snapshot(markers ?? []);
        Settings = settings ?? ReplayPaintSettings.Default(ReplayExtent.Measure(allReportedContacts));
        Validate(Settings, replay.Count);
        legendOccupancy = ReplayLegendOccupancySummary.Create(allReportedContacts, Settings.PanelExtent);
        annotationIndex = ReplayPaintAnnotationIndex.Create(this.diagnostics, this.markers, replay.Count);
        ResolvedLegendPosition = ResolveLegendPosition(Settings);
    }

    /// <summary>Gets the replay session used to create Paint scenes.</summary>
    public ITouchReplaySession Replay => replay;
    /// <summary>Gets the trail history associated with the replay's frames.</summary>
    public ReplayTrailHistory TrailHistory => trailHistory;
    /// <summary>Gets the current snapshot of diagnostics used for scene annotations.</summary>
    public IReadOnlyList<ReplayDiagnostic> Diagnostics => diagnostics;
    /// <summary>Gets the current snapshot of markers used for scene annotations.</summary>
    public IReadOnlyList<ReplayMarker> Markers => markers;
    /// <summary>Gets the current validated Paint rendering settings.</summary>
    public ReplayPaintSettings Settings { get; private set; }
    /// <summary>Gets the explicit or automatically selected legend position for the current settings.</summary>
    public ReplayLegendPosition ResolvedLegendPosition { get; private set; }
    /// <summary>Gets the revision counter incremented when Paint settings change.</summary>
    public long Revision { get; private set; }
    /// <summary>Gets the revision counter for settings changes that invalidate cached geometry.</summary>
    public long GeometryRevision { get; private set; }
    /// <summary>Gets the revision counter incremented when diagnostics or markers change.</summary>
    public long AnnotationRevision { get; private set; }

    /// <summary>Validates and applies Paint settings, returning the resulting revisions and required rendering work.</summary>
    public ReplayPaintUpdate Update(ReplayPaintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings, replay.Count);
        if (settings == Settings)
            return new ReplayPaintUpdate(Revision, GeometryRevision, ReplayPaintChangeImpact.None);

        var previous = Settings;
        var impact = Classify(previous, settings);
        var nextRevision = checked(Revision + 1);
        var nextGeometryRevision = (impact & ReplayPaintChangeImpact.Geometry) != 0
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

    /// <summary>Snapshots changed diagnostics and markers, rebuilds their index, and reports the annotation revision.</summary>
    public ReplayPaintAnnotationUpdate UpdateAnnotations(
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyList<ReplayMarker> markers)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(markers);
        if (DiagnosticsEqual(this.diagnostics, diagnostics) && MarkersEqual(this.markers, markers))
            return new ReplayPaintAnnotationUpdate(AnnotationRevision, Changed: false);

        var nextDiagnostics = Snapshot(diagnostics);
        var nextMarkers = Snapshot(markers);
        var nextIndex = ReplayPaintAnnotationIndex.Create(nextDiagnostics, nextMarkers, replay.Count);
        this.diagnostics = nextDiagnostics;
        this.markers = nextMarkers;
        annotationIndex = nextIndex;
        AnnotationRevision = checked(AnnotationRevision + 1);
        return new ReplayPaintAnnotationUpdate(AnnotationRevision, Changed: true);
    }

    /// <summary>Seeks the replay to a logical frame index and creates its projected scene with trails and annotations.</summary>
    public ReplayScene CreateScene(int logicalIndex) => CreateScene(replay.Seek(logicalIndex));

    /// <summary>Creates a projected scene for an in-range snapshot using current Paint settings, trails, and annotations.</summary>
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
        var annotations = annotationIndex.Select(snapshot);
        return ReplaySceneFactory.CreateProjected(
            snapshot,
            replay.Count,
            Settings.PanelExtent,
            annotations.Diagnostics,
            annotations.MarkerLabels,
            trails,
            Settings.ReverseX,
            Settings.ReverseY,
            Settings.SwapAxes);
    }

    /// <summary>Creates renderer settings from the current Paint options, resolved legend position, and frame chrome choice.</summary>
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
                legendOccupancy,
                settings.PanelExtent,
                settings.ReverseX,
                settings.ReverseY,
                settings.SwapAxes)
            : settings.LegendPosition;

    private static ReplayPaintChangeImpact Classify(
        ReplayPaintSettings previous,
        ReplayPaintSettings next)
    {
        var impact = ReplayPaintChangeImpact.Surface;
        if (previous.PointView != next.PointView)
            impact |= ReplayPaintChangeImpact.Inspector;

        var previousHasTrails = previous.TraceVisible || previous.TrailPointsVisible;
        var nextHasTrails = next.TraceVisible || next.TrailPointsVisible;
        if (previous.PanelExtent != next.PanelExtent ||
            previous.TrailRetention != next.TrailRetention ||
            previous.TrailLength != next.TrailLength ||
            previous.TrailVisibilityStart != next.TrailVisibilityStart ||
            previous.ReverseX != next.ReverseX ||
            previous.ReverseY != next.ReverseY ||
            previous.SwapAxes != next.SwapAxes ||
            previousHasTrails != nextHasTrails)
            impact |= ReplayPaintChangeImpact.Scene;

        if (previous.SwapAxes != next.SwapAxes)
            impact |= ReplayPaintChangeImpact.Geometry;
        if (previous.PanelExtent != next.PanelExtent)
            impact |= ReplayPaintChangeImpact.Fit;
        return impact;
    }

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
