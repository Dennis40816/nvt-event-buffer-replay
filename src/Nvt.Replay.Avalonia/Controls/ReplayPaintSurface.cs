using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>Draws one replay scene with its trails, legend and label layout, and handles zoom and pan.</summary>
public sealed class ReplayPaintSurface : Control
{
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);
    private static readonly PaintPalette DarkPalette = PaintPalette.From(ReplayVisualStyle.Dark);
    private static readonly PaintPalette LightPalette = PaintPalette.From(ReplayVisualStyle.Light);
    private PaintPalette Palette => ActualThemeVariant == ThemeVariant.Light ? LightPalette : DarkPalette;
    private IBrush StageBrush => Palette.Stage;
    private IBrush SurfaceBrush => Palette.Surface;
    private IBrush GridBrush => Palette.Grid;
    private IBrush StrongGridBrush => Palette.StrongGrid;
    private IBrush AxisBrush => Palette.Axis;
    private IBrush StrongAxisBrush => Palette.StrongAxis;
    private IBrush AxisLabelBrush => Palette.AxisLabel;
    private IBrush LabelSurfaceBrush => Palette.LabelSurface;
    private IBrush HoverLabelSurfaceBrush => Palette.HoverLabelSurface;
    private IBrush LabelTextBrush => Palette.LabelText;
    private IBrush BreakBrush => Palette.Break;
    private IBrush PalmBrush => Palette.Palm;
    private IBrush AlarmBrush => Palette.Alarm;
    private IBrush InvalidBrush => Palette.Invalid;
    private ReplayScene? scene;
    private readonly LoopCrossfadeState _crossfade;
    private readonly ReplayTrailBatchCache trailBatchCache = new();
    private readonly Dictionary<int, ReplayLabelAnchor> labelAnchors = [];
    private bool labelAnchorsAdvancedForScene;
    private IReadOnlyList<ReplayTrailDrawBatch> sceneTrailBatches = [];
    private readonly DispatcherTimer loopCrossfadeTimer;
    private double zoomFactor = 1;
    private Vector viewportOffset;
    private bool strongGrid;
    private byte? highlightedContactId;
    private readonly PaintOverlayState _overlay = new();
    private readonly PanGesture _pan = new();
    private readonly ReplayLegendPainter _legendPainter;

    /// <summary>Gets which contacts the surface draws: the host state, the reported frame, or both for comparison.</summary>
    public ReplayRenderMode Mode { get; private set; } = ReplayRenderMode.HostState;
    /// <summary>Gets the current zoom factor. 1 is the fitted view.</summary>
    public double ZoomFactor => zoomFactor;
    /// <summary>Gets the id of the contact drawn highlighted, or <see langword="null"/> when none is.</summary>
    public byte? HighlightedContactId => highlightedContactId;
    /// <summary>Gets whether the grid is drawn with the stronger line style.</summary>
    public bool StrongGrid => strongGrid;
    /// <summary>Whether the legend is shown.</summary>
    public bool LegendVisible => _overlay.LegendVisible;
    /// <summary>Whether the legend shows only the one-line contact summary.</summary>
    public bool LegendCollapsed => _overlay.LegendCollapsed;
    /// <summary>The corner the legend is drawn in.</summary>
    public ReplayLegendPosition LegendPosition => _overlay.LegendPosition;
    /// <summary>Whether contact trails are drawn as lines.</summary>
    public bool TrailLinesVisible => _overlay.TrailLinesVisible;
    /// <summary>Whether contact trails are drawn as points.</summary>
    public bool TrailPointsVisible => _overlay.TrailPointsVisible;
    internal int CachedTrailBatchCount => trailBatchCache.Count;
    internal ReplayScene? CurrentScene => scene;
    internal int ScenePresentationCount { get; private set; }
    internal int FitCount { get; private set; }
    /// <summary>Raised when the zoom factor changes, and when a fit resets it.</summary>
    public event EventHandler? ZoomChanged;
    /// <summary>Raised when the legend changes between collapsed and expanded.</summary>
    public event EventHandler? LegendCollapsedChanged;

    /// <summary>Highlights the contact with the given id, or clears the highlight when the id is <see langword="null"/>.</summary>
    public void SetHighlightedContact(byte? contactId)
    {
        if (highlightedContactId == contactId) return;
        highlightedContactId = contactId;
        InvalidateVisual();
    }

    /// <summary>Creates a paint surface using the system timestamp clock.</summary>
    public ReplayPaintSurface() : this(TimeProvider.System.GetTimestamp) { }

    internal ReplayPaintSurface(Func<long> readTimestamp)
    {
        _crossfade = new LoopCrossfadeState(readTimestamp);
        _legendPainter = new ReplayLegendPainter(_overlay);
        SizeChanged += (_, _) => { if (scene is not null && !labelAnchorsAdvancedForScene) AdvanceLabelAnchors(scene); };
        loopCrossfadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        loopCrossfadeTimer.Tick += (_, _) =>
        {
            if (LoopCrossfadeProgress() >= 1)
            {
                _crossfade.End();
                loopCrossfadeTimer.Stop();
            }
            InvalidateVisual();
        };
    }

    /// <summary>Selects which contact source is drawn. A change resets the label placement.</summary>
    public void SetMode(ReplayRenderMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        labelAnchors.Clear();
        labelAnchorsAdvancedForScene = false;
        if (scene is not null) AdvanceLabelAnchors(scene);
        InvalidateVisual();
    }

    /// <summary>Chooses whether contact trails are drawn as lines, as points, both or neither.</summary>
    public void SetTrailVisibility(bool showLines, bool showPoints)
    {
        if (_overlay.TrailLinesVisible == showLines && _overlay.TrailPointsVisible == showPoints) return;
        _overlay.TrailLinesVisible = showLines;
        _overlay.TrailPointsVisible = showPoints;
        InvalidateVisual();
    }

    /// <summary>Shows a scene, optionally fading from the previous scene over the loop transition.</summary>
    public void Show(ReplayScene value, bool crossfade = false)
    {
        ScenePresentationCount++;
        if (crossfade && scene is not null)
        {
            _crossfade.Begin(scene, sceneTrailBatches);
            loopCrossfadeTimer.Start();
        }
        else
        {
            _crossfade.End();
        }
        scene = value;
        labelAnchorsAdvancedForScene = false;
        uint activeIds = 0;
        foreach (var contact in value.ReportedContacts)
            if (contact.Id <= 10) activeIds |= 1u << contact.Id;
        foreach (var contact in value.HostContacts)
            if (contact.Id <= 10) activeIds |= 1u << contact.Id;
        for (var id = 1; id <= 10; id++)
            if ((activeIds & (1u << id)) == 0) labelAnchors.Remove(id);
        sceneTrailBatches = trailBatchCache.Build(value);
        viewportOffset = ClampViewportOffset(viewportOffset, value.ViewExtent, zoomFactor);
        AdvanceLabelAnchors(value);
        UpdatePanCursor();
        InvalidateVisual();
    }

    private void AdvanceLabelAnchors(ReplayScene value)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var bounds = new Rect(Bounds.Size);
        var placements = PlaceSceneLabels(
            BuildViewport(bounds, value.ViewExtent, zoomFactor, viewportOffset),
            BuildAvailableBounds(bounds),
            value).Placements;
        foreach (var placement in placements)
            labelAnchors[placement.Key] = placement.Anchor;
        labelAnchorsAdvancedForScene = true;
    }

    /// <summary>Drops the scene, the trail cache and the label anchors, ends any pan and clears the hover.</summary>
    public void Clear()
    {
        scene = null;
        _crossfade.End();
        sceneTrailBatches = [];
        trailBatchCache.Clear();
        labelAnchors.Clear();
        labelAnchorsAdvancedForScene = false;
        highlightedContactId = null;
        loopCrossfadeTimer.Stop();
        EndPan(releaseCapture: true);
        _overlay.ClearHit();
        UpdatePanCursor();
        InvalidateVisual();
    }

    /// <summary>Zooms in by a factor of 1.25, up to a zoom factor of 8.</summary>
    public void ZoomIn() => SetZoom(zoomFactor * 1.25);

    /// <summary>Zooms out by a factor of 1.25, down to a zoom factor of 0.5.</summary>
    public void ZoomOut() => SetZoom(zoomFactor / 1.25);

    /// <summary>Resets the zoom factor to 1, clears the pan offset and ends any pan in progress.</summary>
    public void Fit()
    {
        FitCount++;
        EndPan(releaseCapture: true);
        viewportOffset = default;
        SetZoom(1, force: true);
    }

    /// <summary>Switches the grid between the normal and the stronger line style.</summary>
    public void SetStrongGrid(bool value)
    {
        if (strongGrid == value) return;
        strongGrid = value;
        InvalidateVisual();
    }

    /// <summary>Places the legend in a corner. <c>Auto</c> resolves to the top-left corner.</summary>
    public void SetLegendPosition(ReplayLegendPosition value)
    {
        var resolved = value == ReplayLegendPosition.Auto ? ReplayLegendPosition.TopLeft : value;
        if (_overlay.LegendPosition == resolved) return;
        _overlay.LegendPosition = resolved;
        InvalidateVisual();
    }

    /// <summary>Shows or hides the legend. Hiding it also clears its hit area and hover state.</summary>
    public void SetLegendVisible(bool value)
    {
        if (_overlay.LegendVisible == value) return;
        _overlay.LegendVisible = value;
        if (!value) _overlay.ClearHit();
        InvalidateVisual();
    }

    /// <summary>Collapses the legend to a one-line summary or expands it, and raises <see cref="LegendCollapsedChanged"/> on change.</summary>
    public void SetLegendCollapsed(bool value)
    {
        if (_overlay.LegendCollapsed == value) return;
        _overlay.LegendCollapsed = value;
        InvalidateVisual();
        LegendCollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetZoom(double value, Point? anchor = null, bool force = false)
    {
        var nextZoom = Math.Clamp(value, 0.5, 8);
        if (!force && Math.Abs(nextZoom - zoomFactor) < 0.001) return;

        if (anchor is { } pointer && scene is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var oldViewport = BuildViewport(new Rect(Bounds.Size), scene.ViewExtent, zoomFactor, viewportOffset);
            var normalizedX = Math.Clamp((pointer.X - oldViewport.Left) / oldViewport.Width, 0, 1);
            var normalizedY = Math.Clamp((pointer.Y - oldViewport.Top) / oldViewport.Height, 0, 1);
            var scale = nextZoom / zoomFactor;
            var newSize = new Size(oldViewport.Width * scale, oldViewport.Height * scale);
            var newTopLeft = new Point(
                pointer.X - (normalizedX * newSize.Width),
                pointer.Y - (normalizedY * newSize.Height));
            var available = BuildAvailableBounds(new Rect(Bounds.Size));
            viewportOffset = new Vector(
                newTopLeft.X + (newSize.Width / 2) - available.Center.X,
                newTopLeft.Y + (newSize.Height / 2) - available.Center.Y);
        }

        zoomFactor = nextZoom;
        if (scene is not null)
            viewportOffset = ClampViewportOffset(viewportOffset, scene.ViewExtent, zoomFactor);
        UpdatePanCursor();
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (scene is null || Math.Abs(e.Delta.Y) < 0.01) return;
        SetZoom(zoomFactor * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), e.GetPosition(this));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_pan.IsPanning && scene is not null)
        {
            viewportOffset = ClampViewportOffset(
                _pan.StartOffset + (position - _pan.Start),
                scene.ViewExtent,
                zoomFactor);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var hovered = _overlay.LegendVisible && _overlay.LegendBounds?.Contains(position) == true;
        if (hovered == _overlay.LegendHovered) return;
        _overlay.LegendHovered = hovered;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_overlay.LegendHovered) return;
        _overlay.LegendHovered = false;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);
        if (_overlay.LegendVisible && _overlay.LegendBounds?.Contains(position) == true)
        {
            SetLegendCollapsed(!_overlay.LegendCollapsed);
            e.Handled = true;
            return;
        }

        if (scene is null || !CanPan(scene.ViewExtent, zoomFactor)) return;

        _pan.IsPanning = true;
        _pan.Start = position;
        _pan.StartOffset = viewportOffset;
        _pan.Pointer = e.Pointer;
        _overlay.LegendHovered = false;
        e.Pointer.Capture(this);
        UpdatePanCursor();
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pan.IsPanning) return;

        EndPan(releaseCapture: true);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPan(releaseCapture: false);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(StageBrush, null, bounds);

        if (scene is null) return;

        var available = BuildAvailableBounds(bounds);
        var viewport = BuildViewport(bounds, scene.ViewExtent, zoomFactor, viewportOffset);
        var legendViewport = BuildViewport(bounds, scene.ViewExtent, 1, default);

        using var clip = context.PushClip(bounds);
        context.DrawRectangle(SurfaceBrush, null, viewport);
        DrawGrid(context, viewport, strongGrid);
        DrawAxisLabels(context, viewport, scene);

        var transition = LoopCrossfadeProgress();
        if (_crossfade.OutgoingScene is { } previous)
        {
            var previousViewport = BuildViewport(bounds, previous.ViewExtent, zoomFactor, viewportOffset);
            var outgoingTrailBatches = _crossfade.OutgoingTrailBatches;
            DrawSceneContacts(context, previousViewport, available, previous, outgoingTrailBatches, 1 - transition);
        }
        DrawSceneContacts(context, viewport, available, scene, sceneTrailBatches, transition);
        _legendPainter.Draw(
            context,
            legendViewport,
            scene,
            Mode,
            new ReplayLegendBrushes(LabelSurfaceBrush, HoverLabelSurfaceBrush, AxisBrush, AxisLabelBrush, LabelTextBrush),
            ContactColor);
    }

    private void DrawSceneContacts(
        DrawingContext context,
        Rect viewport,
        Rect available,
        ReplayScene value,
        IReadOnlyList<ReplayTrailDrawBatch> trailBatches,
        double opacity)
    {
        if (opacity <= 0) return;
        DrawTrails(context, viewport, value, trailBatches, opacity);
        using var opacityScope = context.PushOpacity(Math.Clamp(opacity, 0, 1));

        if (Mode is ReplayRenderMode.ReportedFrame or ReplayRenderMode.Compare)
        {
            foreach (var contact in value.ReportedContacts)
                DrawContact(context, viewport, contact, value, reported: true);
        }
        if (Mode is ReplayRenderMode.HostState or ReplayRenderMode.Compare)
        {
            foreach (var contact in value.HostContacts)
                DrawContact(context, viewport, contact, value, reported: false);
        }

        var (labelData, placements) = PlaceSceneLabels(viewport, available, value);
        for (var index = 0; index < labelData.Length; index++)
            DrawContactLabel(context, labelData[index], placements[index]);

        if (value.GlobalPalm)
            context.DrawRectangle(null, new Pen(AlarmBrush, 4), viewport);
    }

    private (ContactLabelData[] Data, IReadOnlyList<ReplayLabelPlacement> Placements) PlaceSceneLabels(
        Rect viewport, Rect available, ReplayScene value)
    {
        var labels = (Mode == ReplayRenderMode.HostState
            ? value.HostContacts
            : value.ReportedContacts.Concat(value.HostContacts).GroupBy(contact => contact.Id).Select(group => group.First()))
            .OrderBy(contact => contact.Id)
            .ToArray();
        var labelData = new ContactLabelData[labels.Length];
        var requests = new ReplayLabelRequest[labels.Length];
        for (var index = 0; index < labels.Length; index++)
        {
            var data = CreateContactLabelData(viewport, labels[index], value);
            labelData[index] = data;
            requests[index] = new ReplayLabelRequest(
                data.Contact.Id,
                data.Center.X,
                data.Center.Y,
                data.Width,
                data.Height,
                labelAnchors.TryGetValue(data.Contact.Id, out var previousAnchor)
                    ? previousAnchor
                    : null);
        }
        var placements = ReplayLabelLayout.Place(
            new ReplayLabelBounds(available.X + 4, available.Y + 4, available.Width - 8, available.Height - 8),
            requests);
        return (labelData, placements);
    }

    private double LoopCrossfadeProgress() => _crossfade.Progress();

    private static Rect BuildAvailableBounds(Rect bounds)
    {
        const double horizontalMargin = 48;
        const double topMargin = 24;
        const double bottomMargin = 38;
        return new Rect(
            horizontalMargin,
            topMargin,
            Math.Max(1, bounds.Width - (horizontalMargin * 2)),
            Math.Max(1, bounds.Height - topMargin - bottomMargin));
    }

    private static Rect BuildViewport(Rect bounds, ReplayExtent extent, double zoom, Vector offset)
    {
        var available = BuildAvailableBounds(bounds);
        var fitScale = Math.Min(
            available.Width / extent.MaximumX,
            available.Height / extent.MaximumY);
        var viewportWidth = extent.MaximumX * fitScale * zoom;
        var viewportHeight = extent.MaximumY * fitScale * zoom;
        return new Rect(
            available.Center.X + offset.X - (viewportWidth / 2),
            available.Center.Y + offset.Y - (viewportHeight / 2),
            viewportWidth,
            viewportHeight);
    }

    private Vector ClampViewportOffset(Vector offset, ReplayExtent extent, double zoom)
    {
        var bounds = new Rect(Bounds.Size);
        var available = BuildAvailableBounds(bounds);
        var centeredViewport = BuildViewport(bounds, extent, zoom, default);
        var maximumX = Math.Max(0, (centeredViewport.Width - available.Width) / 2);
        var maximumY = Math.Max(0, (centeredViewport.Height - available.Height) / 2);
        return new Vector(
            Math.Clamp(offset.X, -maximumX, maximumX),
            Math.Clamp(offset.Y, -maximumY, maximumY));
    }

    private bool CanPan(ReplayExtent extent, double zoom)
    {
        var bounds = new Rect(Bounds.Size);
        var available = BuildAvailableBounds(bounds);
        var viewport = BuildViewport(bounds, extent, zoom, default);
        return viewport.Width > available.Width + 0.5 || viewport.Height > available.Height + 0.5;
    }

    private void EndPan(bool releaseCapture)
    {
        if (!_pan.IsPanning && _pan.Pointer is null) return;

        var pointer = _pan.Pointer;
        _pan.IsPanning = false;
        _pan.Pointer = null;
        if (releaseCapture)
            pointer?.Capture(null);
        UpdatePanCursor();
    }

    private void UpdatePanCursor()
    {
        Cursor = _pan.IsPanning || (scene is not null && CanPan(scene.ViewExtent, zoomFactor))
            ? PanCursor
            : null;
    }

    private void DrawGrid(DrawingContext context, Rect viewport, bool strong)
    {
        var gridPen = new Pen(strong ? StrongGridBrush : GridBrush, strong ? 1.35 : 1);
        var axisPen = new Pen(strong ? StrongAxisBrush : AxisBrush, strong ? 1.5 : 1);
        context.DrawRectangle(null, axisPen, viewport);
        for (var division = 1; division < 8; division++)
        {
            var x = viewport.X + (viewport.Width * division / 8);
            var y = viewport.Y + (viewport.Height * division / 8);
            context.DrawLine(gridPen, new Point(x, viewport.Y), new Point(x, viewport.Bottom));
            context.DrawLine(gridPen, new Point(viewport.X, y), new Point(viewport.Right, y));
        }
    }

    private void DrawTrails(
        DrawingContext context,
        Rect viewport,
        ReplayScene scene,
        IReadOnlyList<ReplayTrailDrawBatch> batches,
        double opacity)
    {
        var trailLinesVisible = _overlay.TrailLinesVisible;
        var trailPointsVisible = _overlay.TrailPointsVisible;
        if ((!trailLinesVisible && !trailPointsVisible) || batches.Count == 0) return;
        context.Custom(new ReplayTrailDrawOperation(
            viewport,
            scene.ViewExtent,
            batches,
            Palette.ContactColors,
            scene.ReverseX,
            scene.ReverseY,
            trailLinesVisible,
            trailPointsVisible,
            opacity));
    }

    private void DrawContact(
        DrawingContext context,
        Rect viewport,
        ReplayContact contact,
        ReplayScene scene,
        bool reported)
    {
        var center = Project(viewport, scene, contact.X, contact.Y);
        var color = ContactColor(contact.Id);
        var idBrush = new SolidColorBrush(color);
        if (highlightedContactId == contact.Id)
        {
            context.DrawEllipse(null, new Pen(LabelTextBrush, 1.5), center, 19, 19);
            context.DrawEllipse(null, new Pen(idBrush, 2), center, 23, 23);
        }
        if (contact.Invalid)
        {
            const double half = 12;
            var pen = new Pen(InvalidBrush, 3);
            context.DrawLine(pen, center - new Vector(half, half), center + new Vector(half, half));
            context.DrawLine(pen, center + new Vector(half, -half), center + new Vector(-half, half));
            return;
        }

        if (contact.Status == TouchStatus.Break)
        {
            var radius = reported ? 15 : 9;
            var pen = new Pen(BreakBrush, reported ? 3 : 2);
            context.DrawLine(pen, center - new Vector(radius, radius), center + new Vector(radius, radius));
            context.DrawLine(pen, center + new Vector(radius, -radius), center + new Vector(-radius, radius));
            return;
        }

        if (contact.Type == TouchType.Palm)
        {
            var radius = reported ? 16 : 9;
            var rect = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
            context.DrawRectangle(reported ? null : PalmBrush, new Pen(PalmBrush, reported ? 3 : 1), rect);
            return;
        }

        if (reported)
        {
            context.DrawEllipse(null, new Pen(idBrush, 3), center, 15, 15);
            if (contact.Status == TouchStatus.Enter)
                context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)), 2), center, 21, 21);
        }
        else
        {
            context.DrawEllipse(null, new Pen(idBrush, 3), center, 9, 9);
        }
    }

    private ContactLabelData CreateContactLabelData(
        Rect viewport,
        ReplayContact contact,
        ReplayScene scene)
    {
        var center = Project(viewport, scene, contact.X, contact.Y);
        var state = contact.Status switch
        {
            TouchStatus.Enter => "ENTER",
            TouchStatus.Move => "MOVE",
            TouchStatus.Break => "BREAK",
            _ => contact.Type == TouchType.Palm ? "PALM" : "IDLE",
        };
        var headerText = new FormattedText(
            $"ID {contact.Id}  {state}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            13,
            LabelTextBrush);
        var viewCoordinates = scene.ViewCoordinates(contact.X, contact.Y);
        var coordinateText = new FormattedText(
            $"X {viewCoordinates.X:0}  Y {viewCoordinates.Y:0}  {ReplayLegendPainter.TypeLabel(contact.Type).ToUpperInvariant()}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Medium),
            12,
            AxisLabelBrush);
        var width = Math.Max(headerText.Width + 17, coordinateText.Width) + 14;
        var height = headerText.Height + coordinateText.Height + 8;
        return new ContactLabelData(contact, center, headerText, coordinateText, width, height);
    }

    private void DrawContactLabel(
        DrawingContext context,
        ContactLabelData data,
        ReplayLabelPlacement placement)
    {
        var contact = data.Contact;
        var labelRect = new Rect(placement.Bounds.X, placement.Bounds.Y, placement.Bounds.Width, placement.Bounds.Height);
        var idBrush = new SolidColorBrush(ContactColor(contact.Id));
        var leader = new Point(placement.LeaderX, placement.LeaderY);
        context.DrawLine(new Pen(idBrush, 1), data.Center, leader);
        context.DrawRectangle(LabelSurfaceBrush, new Pen(idBrush, 1), labelRect, 2, 2);
        DrawTypeIcon(
            context,
            contact.Type,
            new Point(labelRect.X + 11, labelRect.Y + 3 + (data.HeaderText.Height / 2)),
            idBrush,
            4);
        context.DrawText(data.HeaderText, new Point(labelRect.X + 21, labelRect.Y + 3));
        context.DrawText(data.CoordinateText, new Point(labelRect.X + 7, labelRect.Y + 3 + data.HeaderText.Height));
    }

    private static Point Project(Rect viewport, ReplayScene scene, ushort x, ushort y) =>
        Project(viewport, scene, scene.ViewCoordinates(x, y));

    private static Point Project(Rect viewport, ReplayScene scene, (double X, double Y) coordinates) =>
        new(
            viewport.X + ((scene.ReverseX ? 1 - (coordinates.X / scene.ViewExtent.MaximumX) : coordinates.X / scene.ViewExtent.MaximumX) * viewport.Width),
            viewport.Y + ((scene.ReverseY ? 1 - (coordinates.Y / scene.ViewExtent.MaximumY) : coordinates.Y / scene.ViewExtent.MaximumY) * viewport.Height));

    private void DrawAxisLabels(DrawingContext context, Rect viewport, ReplayScene scene)
    {
        var tickPen = new Pen(StrongAxisBrush, 1.25);
        for (var tick = 0; tick <= 4; tick++)
        {
            var fraction = tick / 4d;
            var x = viewport.Left + (viewport.Width * fraction);
            var y = viewport.Top + (viewport.Height * fraction);
            var xValue = scene.ViewExtent.MaximumX * (scene.ReverseX ? 1 - fraction : fraction);
            var yValue = scene.ViewExtent.MaximumY * (scene.ReverseY ? 1 - fraction : fraction);
            context.DrawLine(tickPen, new Point(x, viewport.Bottom), new Point(x, viewport.Bottom + 6));
            context.DrawLine(tickPen, new Point(viewport.Left - 6, y), new Point(viewport.Left, y));
            DrawAxisText(context, xValue.ToString("0", CultureInfo.InvariantCulture), new Point(x, viewport.Bottom + 8), centered: true);
            DrawAxisText(context, yValue.ToString("0", CultureInfo.InvariantCulture), new Point(viewport.Left - 11, y), rightAligned: true);
        }
        DrawAxisText(context, "X", new Point(viewport.Right + 24, viewport.Bottom + 8));
        DrawAxisText(context, "Y", new Point(viewport.Left - 11, viewport.Top - 20), rightAligned: true);
    }

    private void DrawAxisText(
        DrawingContext context,
        string value,
        Point origin,
        bool centered = false,
        bool rightAligned = false)
    {
        var text = new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.SemiBold),
            13,
            AxisLabelBrush);
        var x = centered ? origin.X - (text.Width / 2) : rightAligned ? origin.X - text.Width : origin.X;
        var y = rightAligned ? origin.Y - (text.Height / 2) : origin.Y;
        context.DrawText(text, new Point(x, y));
    }

    internal static void DrawTypeIcon(
        DrawingContext context,
        TouchType type,
        Point center,
        IBrush brush,
        double radius)
    {
        switch (type)
        {
            case TouchType.Finger:
                context.DrawEllipse(brush, null, center, radius, radius);
                break;
            case TouchType.Glove:
                DrawFilledPolygon(
                    context,
                    brush,
                    new Point(center.X, center.Y - radius),
                    new Point(center.X + radius, center.Y),
                    new Point(center.X, center.Y + radius),
                    new Point(center.X - radius, center.Y));
                break;
            case TouchType.Palm:
                context.DrawRectangle(brush, null, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
                break;
            default:
                DrawFilledPolygon(
                    context,
                    brush,
                    new Point(center.X, center.Y - radius),
                    new Point(center.X + radius, center.Y + radius),
                    new Point(center.X - radius, center.Y + radius));
                break;
        }
    }

    private static void DrawFilledPolygon(DrawingContext context, IBrush brush, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(points[0], isFilled: true);
            foreach (var point in points.Skip(1))
            {
                path.LineTo(point);
            }
            path.EndFigure(isClosed: true);
        }
        context.DrawGeometry(brush, null, geometry);
    }

    private Color ContactColor(byte id) => Palette.ContactColors[(Math.Max(1, (int)id) - 1) % Palette.ContactColors.Count];

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

    private sealed record ContactLabelData(
        ReplayContact Contact,
        Point Center,
        FormattedText HeaderText,
        FormattedText CoordinateText,
        double Width,
        double Height);

    private sealed record PaintPalette(
        IBrush Stage,
        IBrush Surface,
        IBrush Grid,
        IBrush StrongGrid,
        IBrush Axis,
        IBrush StrongAxis,
        IBrush AxisLabel,
        IBrush LabelSurface,
        IBrush HoverLabelSurface,
        IBrush LabelText,
        IBrush Break,
        IBrush Palm,
        IBrush Alarm,
        IBrush Invalid,
        IReadOnlyList<Color> ContactColors)
    {
        public static PaintPalette From(ReplayVisualPalette source) => new(
            Brush(source.Stage.Hex),
            Brush(source.Surface.Hex),
            Brush(source.Grid.Hex),
            Brush(source.StrongGrid.Hex),
            Brush(source.Axis.Hex),
            Brush(source.StrongAxis.Hex),
            Brush(source.AxisLabel.Hex),
            Brush(source.LabelSurface.Hex),
            Brush(source.HoverLabelSurface.Hex),
            Brush(source.LabelText.Hex),
            Brush(source.Break.Hex),
            Brush(source.Palm.Hex),
            Brush(source.Alarm.Hex),
            Brush(source.Invalid.Hex),
            source.ContactColors.Select(color => Color.Parse(color.Hex)).ToArray());
    }
}
