using System.Globalization;
using System.Diagnostics;
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

public sealed class ReplayPaintSurface : Control
{
    private static readonly Cursor PanCursor = new(StandardCursorType.SizeAll);
    private static readonly TimeSpan LoopCrossfadeDuration = TimeSpan.FromMilliseconds(140);

    private static readonly PaintPalette DarkPalette = new(
        Stage: Brush("#10171A"),
        Surface: Brush("#162428"),
        Grid: Brush("#26383D"),
        StrongGrid: Brush("#34464F"),
        Axis: Brush("#324149"),
        StrongAxis: Brush("#60747D"),
        AxisLabel: Brush("#829097"),
        LabelSurface: Brush("#EE101619"),
        HoverLabelSurface: Brush("#F5101619"),
        LabelText: Brush("#F3F7F5"),
        Break: Brush("#F1A85B"),
        Palm: Brush("#D98AC6"),
        Alarm: Brush("#E86A64"),
        Invalid: Brush("#F06B6B"),
        ContactColors:
        [
            Color.Parse("#67D5E4"), Color.Parse("#C8F36C"), Color.Parse("#FFBE6B"),
            Color.Parse("#BBA2FF"), Color.Parse("#FF7F91"), Color.Parse("#78A9FF"),
            Color.Parse("#66D39A"), Color.Parse("#E48CE0"), Color.Parse("#F2DD70"),
            Color.Parse("#76D7B7"),
        ]);

    private static readonly PaintPalette LightPalette = new(
        Stage: Brush("#E9EFEC"),
        Surface: Brush("#F7FAF8"),
        Grid: Brush("#DCE5E1"),
        StrongGrid: Brush("#C8D5D0"),
        Axis: Brush("#B5C6BF"),
        StrongAxis: Brush("#819890"),
        AxisLabel: Brush("#586D64"),
        LabelSurface: Brush("#F7FFFFFF"),
        HoverLabelSurface: Brush("#FFFFFFFF"),
        LabelText: Brush("#182821"),
        Break: Brush("#A65D10"),
        Palm: Brush("#8F467D"),
        Alarm: Brush("#B63D3A"),
        Invalid: Brush("#C13D3D"),
        ContactColors:
        [
            Color.Parse("#007C91"), Color.Parse("#5D8100"), Color.Parse("#B56500"),
            Color.Parse("#6848C7"), Color.Parse("#C53A59"), Color.Parse("#3567C8"),
            Color.Parse("#27855A"), Color.Parse("#A14299"), Color.Parse("#8E7600"),
            Color.Parse("#177D68"),
        ]);

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
    private ReplayScene? outgoingScene;
    private readonly DispatcherTimer loopCrossfadeTimer;
    private long loopCrossfadeStarted;
    private double zoomFactor = 1;
    private Vector viewportOffset;
    private bool strongGrid;
    private ReplayLegendPosition legendPosition = ReplayLegendPosition.TopLeft;
    private bool legendVisible = true;
    private bool legendCollapsed = true;
    private bool legendHovered;
    private Rect? legendBounds;
    private bool isPanning;
    private Point panStart;
    private Vector panStartOffset;
    private IPointer? panPointer;

    public ReplayRenderMode Mode { get; private set; } = ReplayRenderMode.HostState;
    public double ZoomFactor => zoomFactor;
    public bool StrongGrid => strongGrid;
    public bool LegendVisible => legendVisible;
    public bool LegendCollapsed => legendCollapsed;
    public event EventHandler? ZoomChanged;
    public event EventHandler? LegendCollapsedChanged;

    public ReplayPaintSurface()
    {
        loopCrossfadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        loopCrossfadeTimer.Tick += (_, _) =>
        {
            if (LoopCrossfadeProgress() >= 1)
            {
                outgoingScene = null;
                loopCrossfadeTimer.Stop();
            }
            InvalidateVisual();
        };
    }

    public void SetMode(ReplayRenderMode mode)
    {
        Mode = mode;
        InvalidateVisual();
    }

    public void Show(ReplayScene value, bool crossfade = false)
    {
        if (crossfade && scene is not null)
        {
            outgoingScene = scene;
            loopCrossfadeStarted = Stopwatch.GetTimestamp();
            loopCrossfadeTimer.Start();
        }
        scene = value;
        viewportOffset = ClampViewportOffset(viewportOffset, value.Extent, zoomFactor);
        UpdatePanCursor();
        InvalidateVisual();
    }

    public void Clear()
    {
        scene = null;
        outgoingScene = null;
        loopCrossfadeTimer.Stop();
        EndPan(releaseCapture: true);
        legendBounds = null;
        legendHovered = false;
        UpdatePanCursor();
        InvalidateVisual();
    }

    public void ZoomIn() => SetZoom(zoomFactor * 1.25);

    public void ZoomOut() => SetZoom(zoomFactor / 1.25);

    public void Fit()
    {
        EndPan(releaseCapture: true);
        viewportOffset = default;
        SetZoom(1, force: true);
    }

    public void SetStrongGrid(bool value)
    {
        strongGrid = value;
        InvalidateVisual();
    }

    public void SetLegendPosition(ReplayLegendPosition value)
    {
        legendPosition = value == ReplayLegendPosition.Auto ? ReplayLegendPosition.TopLeft : value;
        InvalidateVisual();
    }

    public void SetLegendVisible(bool value)
    {
        if (legendVisible == value) return;
        legendVisible = value;
        if (!value)
        {
            legendBounds = null;
            legendHovered = false;
        }
        InvalidateVisual();
    }

    public void SetLegendCollapsed(bool value)
    {
        if (legendCollapsed == value) return;
        legendCollapsed = value;
        InvalidateVisual();
        LegendCollapsedChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetZoom(double value, Point? anchor = null, bool force = false)
    {
        var nextZoom = Math.Clamp(value, 0.5, 8);
        if (!force && Math.Abs(nextZoom - zoomFactor) < 0.001) return;

        if (anchor is { } pointer && scene is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            var oldViewport = BuildViewport(new Rect(Bounds.Size), scene.Extent, zoomFactor, viewportOffset);
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
            viewportOffset = ClampViewportOffset(viewportOffset, scene.Extent, zoomFactor);
        UpdatePanCursor();
        InvalidateVisual();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (scene is null || Math.Abs(e.Delta.Y) < 0.01) return;
        SetZoom(zoomFactor * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (isPanning && scene is not null)
        {
            viewportOffset = ClampViewportOffset(
                panStartOffset + (position - panStart),
                scene.Extent,
                zoomFactor);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var hovered = legendVisible && legendBounds?.Contains(position) == true;
        if (hovered == legendHovered) return;
        legendHovered = hovered;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!legendHovered) return;
        legendHovered = false;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var position = e.GetPosition(this);
        if (legendVisible && legendBounds?.Contains(position) == true)
        {
            SetLegendCollapsed(!legendCollapsed);
            e.Handled = true;
            return;
        }

        if (scene is null || !CanPan(scene.Extent, zoomFactor)) return;

        isPanning = true;
        panStart = position;
        panStartOffset = viewportOffset;
        panPointer = e.Pointer;
        legendHovered = false;
        e.Pointer.Capture(this);
        UpdatePanCursor();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!isPanning) return;

        EndPan(releaseCapture: true);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndPan(releaseCapture: false);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(StageBrush, null, bounds);

        if (scene is null) return;

        var available = BuildAvailableBounds(bounds);
        var viewport = BuildViewport(bounds, scene.Extent, zoomFactor, viewportOffset);
        var legendViewport = BuildViewport(bounds, scene.Extent, 1, default);

        using var clip = context.PushClip(bounds);
        context.DrawRectangle(SurfaceBrush, null, viewport);
        DrawGrid(context, viewport, strongGrid);
        DrawAxisLabels(context, viewport, scene);

        var transition = LoopCrossfadeProgress();
        if (outgoingScene is { } previous)
        {
            var previousViewport = BuildViewport(bounds, previous.Extent, zoomFactor, viewportOffset);
            DrawSceneContacts(context, previousViewport, available, previous, 1 - transition);
        }
        DrawSceneContacts(context, viewport, available, scene, transition);
        DrawLegend(context, legendViewport, scene);
    }

    private void DrawSceneContacts(DrawingContext context, Rect viewport, Rect available, ReplayScene value, double opacity)
    {
        if (opacity <= 0) return;
        using var opacityScope = context.PushOpacity(Math.Clamp(opacity, 0, 1));
        DrawTrails(context, viewport, value);

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

        var labels = (Mode == ReplayRenderMode.HostState
            ? value.HostContacts
            : value.ReportedContacts.Concat(value.HostContacts).GroupBy(contact => contact.Id).Select(group => group.First()))
            .OrderBy(contact => contact.Id)
            .ToArray();
        var protectedPoints = labels
            .Select(contact => Project(viewport, value, contact.X, contact.Y))
            .Select(center => new Rect(center.X - 23, center.Y - 23, 46, 46))
            .ToArray();
        var occupiedLabels = new List<Rect>();
        foreach (var contact in labels)
            DrawContactLabel(context, viewport, available, contact, value, protectedPoints, occupiedLabels);

        if (value.GlobalPalm)
            context.DrawRectangle(null, new Pen(AlarmBrush, 4), viewport);
    }

    private double LoopCrossfadeProgress()
    {
        if (outgoingScene is null || loopCrossfadeStarted == 0) return 1;
        return Math.Clamp(
            Stopwatch.GetElapsedTime(loopCrossfadeStarted).TotalMilliseconds / LoopCrossfadeDuration.TotalMilliseconds,
            0,
            1);
    }

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
        if (!isPanning && panPointer is null) return;

        var pointer = panPointer;
        isPanning = false;
        panPointer = null;
        if (releaseCapture)
            pointer?.Capture(null);
        UpdatePanCursor();
    }

    private void UpdatePanCursor()
    {
        Cursor = isPanning || (scene is not null && CanPan(scene.Extent, zoomFactor))
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

    private void DrawTrails(DrawingContext context, Rect viewport, ReplayScene scene)
    {
        foreach (var trail in scene.ContactTrails)
        {
            var color = ContactColor(trail.Id);
            var brush = new SolidColorBrush(color);
            for (var index = 1; index < trail.Points.Count; index++)
            {
                context.DrawLine(
                    new Pen(brush, 2.5),
                    Project(viewport, scene, trail.Points[index - 1].X, trail.Points[index - 1].Y),
                    Project(viewport, scene, trail.Points[index].X, trail.Points[index].Y));
            }
        }
    }

    private void DrawLegend(DrawingContext context, Rect viewport, ReplayScene scene)
    {
        if (!legendVisible)
        {
            legendBounds = null;
            return;
        }

        var entries = scene.ReportedContacts
            .Concat(scene.HostContacts)
            .Select(contact => (contact.Id, contact.Type))
            .Concat(scene.ContactTrails.Select(trail => (trail.Id, trail.Type)))
            .GroupBy(entry => entry.Id)
            .Select(group => group.First())
            .OrderBy(entry => entry.Id)
            .Take(10)
            .ToArray();
        var activeContacts = (Mode switch
            {
                ReplayRenderMode.HostState => scene.HostContacts,
                ReplayRenderMode.ReportedFrame => scene.ReportedContacts,
                _ => scene.ReportedContacts.Concat(scene.HostContacts).ToArray(),
            })
            .Where(contact => contact.IsActive)
            .GroupBy(contact => contact.Id)
            .Select(group => group.First())
            .OrderBy(contact => contact.Id)
            .Take(10)
            .ToArray();
        if (entries.Length == 0)
        {
            legendBounds = null;
            return;
        }

        if (legendCollapsed)
        {
            DrawCollapsedLegend(context, viewport, activeContacts);
            return;
        }

        const double headerHeight = 26;
        const double itemHeight = 22;
        const double bottomPadding = 7;
        var legendRect = PositionLegend(
            viewport,
            new Size(140, headerHeight + (entries.Length * itemHeight) + bottomPadding));
        legendBounds = legendRect;
        context.DrawRectangle(legendHovered ? HoverLabelSurfaceBrush : LabelSurfaceBrush, new Pen(AxisBrush, 1), legendRect, 3, 3);

        var heading = new FormattedText(
            "CONTACTS",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            11,
            AxisLabelBrush);
        var headingBounds = new Rect(legendRect.X + 10, legendRect.Y, legendRect.Width - 20, headerHeight);
        context.DrawText(heading, new Point(headingBounds.X, CenteredTextY(headingBounds, heading)));

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var rowBounds = new Rect(
                legendRect.X + 10,
                legendRect.Y + headerHeight + (index * itemHeight),
                legendRect.Width - 20,
                itemHeight);
            var color = ContactColor(entry.Id);
            var brush = new SolidColorBrush(color);
            context.DrawEllipse(brush, null, new Point(rowBounds.X + 4, rowBounds.Center.Y), 3.5, 3.5);
            var text = new FormattedText(
                $"#{entry.Id}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.SemiBold),
                12,
                LabelTextBrush);
            context.DrawText(text, new Point(rowBounds.X + 11, CenteredTextY(rowBounds, text)));
            DrawLegendType(
                context,
                new Rect(rowBounds.X + 48, rowBounds.Y, rowBounds.Width - 48, rowBounds.Height),
                entry.Type,
                TypeLabel(entry.Type));
        }
    }

    private void DrawCollapsedLegend(
        DrawingContext context,
        Rect viewport,
        IReadOnlyList<ReplayContact> activeContacts)
    {
        var summary = ContactSummary(activeContacts);
        var text = new FormattedText(
            summary,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            12,
            LabelTextBrush);
        var legendRect = PositionLegend(viewport, new Size(text.Width + 37, 30));
        legendBounds = legendRect;
        context.DrawRectangle(legendHovered ? HoverLabelSurfaceBrush : LabelSurfaceBrush, new Pen(AxisBrush, 1), legendRect, 15, 15);
        context.DrawEllipse(AxisLabelBrush, null, new Point(legendRect.X + 13, legendRect.Center.Y), 3.5, 3.5);
        var textBounds = new Rect(legendRect.X + 25, legendRect.Y, legendRect.Width - 31, legendRect.Height);
        context.DrawText(text, new Point(textBounds.X, CenteredTextY(textBounds, text)));
    }

    private static string ContactSummary(IReadOnlyList<ReplayContact> activeContacts)
    {
        var fingers = activeContacts.Count(contact => contact.Type == TouchType.Finger);
        var gloves = activeContacts.Count(contact => contact.Type == TouchType.Glove);
        var palms = activeContacts.Count(contact => contact.Type == TouchType.Palm);
        var other = activeContacts.Count - fingers - gloves - palms;
        var parts = new List<string>(4);
        AddContactCount(parts, fingers, "finger");
        AddContactCount(parts, gloves, "glove");
        AddContactCount(parts, palms, "palm");
        AddContactCount(parts, other, "other", pluralize: false);
        return parts.Count == 0 ? "0 contacts" : string.Join(" · ", parts);
    }

    private static void AddContactCount(
        ICollection<string> parts,
        int count,
        string label,
        bool pluralize = true)
    {
        if (count == 0) return;
        parts.Add($"{count} {label}{(pluralize && count != 1 ? "s" : string.Empty)}");
    }

    private Rect PositionLegend(Rect viewport, Size size)
    {
        const double inset = 12;
        var rightAligned = legendPosition is ReplayLegendPosition.TopRight or ReplayLegendPosition.BottomRight;
        var bottomAligned = legendPosition is ReplayLegendPosition.BottomLeft or ReplayLegendPosition.BottomRight;
        var x = rightAligned ? viewport.Right - inset - size.Width : viewport.Left + inset;
        var y = bottomAligned ? viewport.Bottom - inset - size.Height : viewport.Top + inset;
        return new Rect(x, y, size.Width, size.Height);
    }

    private static string TypeLabel(TouchType type) => type switch
    {
        TouchType.Finger => "finger",
        TouchType.Glove => "glove",
        TouchType.Palm => "palm",
        _ => "other",
    };

    private void DrawLegendType(DrawingContext context, Rect bounds, TouchType type, string label)
    {
        DrawTypeIcon(context, type, new Point(bounds.X + 4, bounds.Center.Y), AxisLabelBrush, 3.5);
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Medium),
            11,
            AxisLabelBrush);
        context.DrawText(text, new Point(bounds.X + 11, CenteredTextY(bounds, text)));
    }

    private static double CenteredTextY(Rect bounds, FormattedText text) =>
        Math.Round(bounds.Y + ((bounds.Height - text.Height) / 2), MidpointRounding.AwayFromZero);

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
            context.DrawEllipse(idBrush, new Pen(SurfaceBrush, 2), center, 8, 8);
        }
    }

    private void DrawContactLabel(
        DrawingContext context,
        Rect viewport,
        Rect labelBounds,
        ReplayContact contact,
        ReplayScene scene,
        IReadOnlyList<Rect> protectedPoints,
        ICollection<Rect> occupiedLabels)
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
        var coordinateText = new FormattedText(
            $"X {contact.X}  Y {contact.Y}  ·  {TypeLabel(contact.Type)}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Medium),
            12,
            AxisLabelBrush);
        var width = Math.Max(headerText.Width + 17, coordinateText.Width) + 14;
        var height = headerText.Height + coordinateText.Height + 8;
        const double pointClearance = 27;
        var candidateOrigins = new[]
        {
            new Point(center.X + pointClearance, center.Y - (height / 2)),
            new Point(center.X - width - pointClearance, center.Y - (height / 2)),
            new Point(center.X - (width / 2), center.Y - height - pointClearance),
            new Point(center.X - (width / 2), center.Y + pointClearance),
            new Point(center.X + 19, center.Y - height - 19),
            new Point(center.X - width - 19, center.Y - height - 19),
            new Point(center.X + 19, center.Y + 19),
            new Point(center.X - width - 19, center.Y + 19),
        };
        var labelRect = candidateOrigins
            .Select((origin, priority) =>
            {
                var candidate = new Rect(
                    Math.Clamp(origin.X, labelBounds.Left + 4, labelBounds.Right - width - 4),
                    Math.Clamp(origin.Y, labelBounds.Top + 4, labelBounds.Bottom - height - 4),
                    width,
                    height);
                var score = priority;
                score += protectedPoints.Sum(point => OverlapPenalty(point, candidate, 100_000));
                score += occupiedLabels.Sum(existing => OverlapPenalty(existing, candidate, 10_000));
                score += (int)(Math.Abs(candidate.X - origin.X) + Math.Abs(candidate.Y - origin.Y)) * 100;
                return (candidate, score);
            })
            .OrderBy(result => result.score)
            .First().candidate;
        occupiedLabels.Add(labelRect);
        var idBrush = new SolidColorBrush(ContactColor(contact.Id));
        var leaderEnd = new Point(
            Math.Clamp(center.X, labelRect.Left, labelRect.Right),
            Math.Clamp(center.Y, labelRect.Top, labelRect.Bottom));
        context.DrawLine(new Pen(idBrush, 1), center, leaderEnd);
        context.DrawRectangle(LabelSurfaceBrush, new Pen(idBrush, 1), labelRect);
        DrawTypeIcon(
            context,
            contact.Type,
            new Point(labelRect.X + 11, labelRect.Y + 3 + (headerText.Height / 2)),
            idBrush,
            4);
        context.DrawText(headerText, new Point(labelRect.X + 21, labelRect.Y + 3));
        context.DrawText(coordinateText, new Point(labelRect.X + 7, labelRect.Y + 3 + headerText.Height));
    }

    private static Point Project(Rect viewport, ReplayScene scene, ushort x, ushort y) =>
        Project(viewport, scene.Extent, x, y, scene.ReverseX, scene.ReverseY);

    private static Point Project(Rect viewport, ReplayExtent extent, ushort x, ushort y, bool reverseX, bool reverseY) =>
        new(
            viewport.X + ((reverseX ? 1 - (x / extent.MaximumX) : x / extent.MaximumX) * viewport.Width),
            viewport.Y + ((reverseY ? 1 - (y / extent.MaximumY) : y / extent.MaximumY) * viewport.Height));

    private void DrawAxisLabels(DrawingContext context, Rect viewport, ReplayScene scene)
    {
        var tickPen = new Pen(StrongAxisBrush, 1.25);
        for (var tick = 0; tick <= 4; tick++)
        {
            var fraction = tick / 4d;
            var x = viewport.Left + (viewport.Width * fraction);
            var y = viewport.Top + (viewport.Height * fraction);
            var xValue = scene.Extent.MaximumX * (scene.ReverseX ? 1 - fraction : fraction);
            var yValue = scene.Extent.MaximumY * (scene.ReverseY ? 1 - fraction : fraction);
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
            12,
            AxisLabelBrush);
        var x = centered ? origin.X - (text.Width / 2) : rightAligned ? origin.X - text.Width : origin.X;
        var y = rightAligned ? origin.Y - (text.Height / 2) : origin.Y;
        context.DrawText(text, new Point(x, y));
    }

    private static bool Overlaps(Rect first, Rect second) =>
        first.Left < second.Right && first.Right > second.Left &&
        first.Top < second.Bottom && first.Bottom > second.Top;

    private static int OverlapPenalty(Rect first, Rect second, int basePenalty)
    {
        if (!Overlaps(first, second)) return 0;
        var width = Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left);
        var height = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top);
        return basePenalty + (int)Math.Ceiling(width * height);
    }

    private static void DrawTypeIcon(
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
        IReadOnlyList<Color> ContactColors);
}
