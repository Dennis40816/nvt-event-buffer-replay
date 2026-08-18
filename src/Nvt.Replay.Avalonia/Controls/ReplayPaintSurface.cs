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
    private byte? highlightedContactId;
    private Point panStart;
    private Vector panStartOffset;
    private IPointer? panPointer;

    public ReplayRenderMode Mode { get; private set; } = ReplayRenderMode.HostState;
    public double ZoomFactor => zoomFactor;
    public byte? HighlightedContactId => highlightedContactId;
    public bool StrongGrid => strongGrid;
    public bool LegendVisible => legendVisible;
    public bool LegendCollapsed => legendCollapsed;
    public ReplayLegendPosition LegendPosition => legendPosition;
    internal ReplayScene? CurrentScene => scene;
    public event EventHandler? ZoomChanged;
    public event EventHandler? LegendCollapsedChanged;

    public void SetHighlightedContact(byte? contactId)
    {
        if (highlightedContactId == contactId) return;
        highlightedContactId = contactId;
        InvalidateVisual();
    }

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
        viewportOffset = ClampViewportOffset(viewportOffset, value.ViewExtent, zoomFactor);
        UpdatePanCursor();
        InvalidateVisual();
    }

    public void Clear()
    {
        scene = null;
        outgoingScene = null;
        highlightedContactId = null;
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
                scene.ViewExtent,
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

        if (scene is null || !CanPan(scene.ViewExtent, zoomFactor)) return;

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
        var viewport = BuildViewport(bounds, scene.ViewExtent, zoomFactor, viewportOffset);
        var legendViewport = BuildViewport(bounds, scene.ViewExtent, 1, default);

        using var clip = context.PushClip(bounds);
        context.DrawRectangle(SurfaceBrush, null, viewport);
        DrawGrid(context, viewport, strongGrid);
        DrawAxisLabels(context, viewport, scene);

        var transition = LoopCrossfadeProgress();
        if (outgoingScene is { } previous)
        {
            var previousViewport = BuildViewport(bounds, previous.ViewExtent, zoomFactor, viewportOffset);
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
        var labelData = labels
            .Select(contact => CreateContactLabelData(viewport, contact, value))
            .ToArray();
        var placements = ReplayLabelLayout.Place(
                new ReplayLabelBounds(available.X + 4, available.Y + 4, available.Width - 8, available.Height - 8),
                labelData.Select(data => new ReplayLabelRequest(
                    data.Contact.Id,
                    data.Center.X,
                    data.Center.Y,
                    data.Width,
                    data.Height)).ToArray())
            .ToDictionary(placement => placement.Key);
        foreach (var data in labelData)
            DrawContactLabel(context, data, placements[data.Contact.Id]);

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
        Cursor = isPanning || (scene is not null && CanPan(scene.ViewExtent, zoomFactor))
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

        const double headerHeight = 28;
        const double itemHeight = 24;
        const double bottomPadding = 7;
        var legendRect = PositionLegend(
            viewport,
            new Size(150, headerHeight + (entries.Length * itemHeight) + bottomPadding));
        legendBounds = legendRect;
        context.DrawRectangle(legendHovered ? HoverLabelSurfaceBrush : LabelSurfaceBrush, new Pen(AxisBrush, 1), legendRect, 3, 3);

        var heading = new FormattedText(
            "CONTACTS",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            12.5,
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
                13,
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
            13,
            LabelTextBrush);
        var legendRect = PositionLegend(viewport, new Size(text.Width + 39, 32));
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
        return parts.Count == 0 ? "0 contacts" : string.Join("  |  ", parts);
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
            12.5,
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
            context.DrawEllipse(idBrush, new Pen(SurfaceBrush, 2), center, 8, 8);
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
            14,
            LabelTextBrush);
        var viewCoordinates = scene.ViewCoordinates(contact.X, contact.Y);
        var coordinateText = new FormattedText(
            $"X {viewCoordinates.X:0}   Y {viewCoordinates.Y:0}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Medium),
            13,
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
        var labelRect = new Rect(
            placement.Bounds.X,
            placement.Bounds.Y,
            placement.Bounds.Width,
            placement.Bounds.Height);
        var idBrush = new SolidColorBrush(ContactColor(contact.Id));
        context.DrawLine(new Pen(idBrush, 1), data.Center, new Point(placement.LeaderX, placement.LeaderY));
        var selected = highlightedContactId == contact.Id;
        context.DrawRectangle(
            selected ? HoverLabelSurfaceBrush : LabelSurfaceBrush,
            new Pen(idBrush, selected ? 2 : 1),
            labelRect);
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
