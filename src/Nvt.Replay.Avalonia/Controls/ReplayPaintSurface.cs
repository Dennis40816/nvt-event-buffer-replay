using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class ReplayPaintSurface : Control
{
    private static readonly IBrush SurfaceBrush = Brush("#090D0F");
    private static readonly IBrush GridBrush = Brush("#182227");
    private static readonly IBrush AxisBrush = Brush("#26343A");
    private static readonly IBrush LabelSurfaceBrush = Brush("#E612171A");
    private static readonly IBrush LabelTextBrush = Brush("#F3F7F5");
    private static readonly IBrush BreakBrush = Brush("#F1A85B");
    private static readonly IBrush PalmBrush = Brush("#D98AC6");
    private static readonly IBrush AlarmBrush = Brush("#E86A64");
    private static readonly IBrush InvalidBrush = Brush("#F06B6B");
    private static readonly Color[] ContactColors =
    [
        Color.Parse("#67D5E4"),
        Color.Parse("#C8F36C"),
        Color.Parse("#FFBE6B"),
        Color.Parse("#BBA2FF"),
        Color.Parse("#FF7F91"),
        Color.Parse("#78A9FF"),
        Color.Parse("#66D39A"),
        Color.Parse("#E48CE0"),
        Color.Parse("#F2DD70"),
        Color.Parse("#76D7B7"),
    ];

    private ReplayScene? scene;
    private double zoomFactor = 1;

    public ReplayRenderMode Mode { get; private set; } = ReplayRenderMode.Compare;
    public double ZoomFactor => zoomFactor;

    public void SetMode(ReplayRenderMode mode)
    {
        Mode = mode;
        InvalidateVisual();
    }

    public void Show(ReplayScene value)
    {
        scene = value;
        InvalidateVisual();
    }

    public void Clear()
    {
        scene = null;
        InvalidateVisual();
    }

    public void ZoomIn() => SetZoom(zoomFactor * 1.25);

    public void ZoomOut() => SetZoom(zoomFactor / 1.25);

    public void Fit() => SetZoom(1);

    private void SetZoom(double value)
    {
        zoomFactor = Math.Clamp(value, 0.5, 8);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(SurfaceBrush, null, bounds);

        if (scene is null) return;

        const double horizontalMargin = 42;
        const double topMargin = 34;
        const double bottomMargin = 38;
        var available = new Rect(
            horizontalMargin,
            topMargin,
            Math.Max(1, bounds.Width - (horizontalMargin * 2)),
            Math.Max(1, bounds.Height - topMargin - bottomMargin));
        var fitScale = Math.Min(
            available.Width / scene.Extent.MaximumX,
            available.Height / scene.Extent.MaximumY);
        var viewportWidth = scene.Extent.MaximumX * fitScale * zoomFactor;
        var viewportHeight = scene.Extent.MaximumY * fitScale * zoomFactor;
        var viewport = new Rect(
            available.Center.X - (viewportWidth / 2),
            available.Center.Y - (viewportHeight / 2),
            viewportWidth,
            viewportHeight);

        using var clip = context.PushClip(bounds);
        DrawGrid(context, viewport);

        DrawTrails(context, viewport, scene);

        if (Mode is ReplayRenderMode.ReportedFrame or ReplayRenderMode.Compare)
        {
            foreach (var contact in scene.ReportedContacts)
                DrawContact(context, viewport, contact, scene.Extent, reported: true);
        }
        if (Mode is ReplayRenderMode.HostState or ReplayRenderMode.Compare)
        {
            foreach (var contact in scene.HostContacts)
                DrawContact(context, viewport, contact, scene.Extent, reported: false);
        }

        var labels = Mode == ReplayRenderMode.HostState
            ? scene.HostContacts
            : scene.ReportedContacts.Concat(scene.HostContacts).GroupBy(contact => contact.Id).Select(group => group.First());
        foreach (var contact in labels.OrderBy(contact => contact.Id))
            DrawContactLabel(context, viewport, available, contact, scene.Extent);

        if (scene.GlobalPalm)
            context.DrawRectangle(null, new Pen(AlarmBrush, 4), viewport);
    }

    private static void DrawGrid(DrawingContext context, Rect viewport)
    {
        context.DrawRectangle(null, new Pen(AxisBrush, 1), viewport);
        for (var division = 1; division < 8; division++)
        {
            var x = viewport.X + (viewport.Width * division / 8);
            var y = viewport.Y + (viewport.Height * division / 8);
            context.DrawLine(new Pen(GridBrush, 1), new Point(x, viewport.Y), new Point(x, viewport.Bottom));
            context.DrawLine(new Pen(GridBrush, 1), new Point(viewport.X, y), new Point(viewport.Right, y));
        }
    }

    private static void DrawTrails(DrawingContext context, Rect viewport, ReplayScene scene)
    {
        foreach (var trail in scene.ContactTrails)
        {
            var color = ContactColor(trail.Id);
            var brush = new SolidColorBrush(color);
            for (var index = 1; index < trail.Points.Count; index++)
            {
                context.DrawLine(
                    new Pen(brush, 2.5),
                    Project(viewport, scene.Extent, trail.Points[index - 1].X, trail.Points[index - 1].Y),
                    Project(viewport, scene.Extent, trail.Points[index].X, trail.Points[index].Y));
            }
        }
    }

    private static void DrawContact(
        DrawingContext context,
        Rect viewport,
        ReplayContact contact,
        ReplayExtent extent,
        bool reported)
    {
        var center = Project(viewport, extent, contact.X, contact.Y);
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

    private static void DrawContactLabel(
        DrawingContext context,
        Rect viewport,
        Rect labelBounds,
        ReplayContact contact,
        ReplayExtent extent)
    {
        var center = Project(viewport, extent, contact.X, contact.Y);
        var state = contact.Status switch
        {
            TouchStatus.Enter => "ENTER",
            TouchStatus.Move => "MOVE",
            TouchStatus.Break => "BREAK",
            _ => contact.Type == TouchType.Palm ? "PALM" : "IDLE",
        };
        var text = new FormattedText(
            $"#{contact.Id}  {state}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold),
            10,
            LabelTextBrush);
        var width = text.Width + 12;
        var height = text.Height + 6;
        var x = Math.Clamp(center.X + 19, labelBounds.Left + 4, labelBounds.Right - width - 4);
        var y = Math.Clamp(center.Y - (height / 2), labelBounds.Top + 4, labelBounds.Bottom - height - 4);
        var labelRect = new Rect(x, y, width, height);
        var idBrush = new SolidColorBrush(ContactColor(contact.Id));
        context.DrawRectangle(LabelSurfaceBrush, new Pen(idBrush, 1), labelRect);
        context.DrawText(text, new Point(x + 6, y + 3));
    }

    private static Point Project(Rect viewport, ReplayExtent extent, ushort x, ushort y) =>
        new(
            viewport.X + (x / extent.MaximumX * viewport.Width),
            viewport.Y + (y / extent.MaximumY * viewport.Height));

    private static Color ContactColor(byte id) => ContactColors[(Math.Max(1, (int)id) - 1) % ContactColors.Length];

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}
