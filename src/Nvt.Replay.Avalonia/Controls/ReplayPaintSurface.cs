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
    private static readonly IBrush SurfaceBrush = Brush("#12191D");
    private static readonly IBrush GridBrush = Brush("#1E2A30");
    private static readonly IBrush StrongGridBrush = Brush("#34464F");
    private static readonly IBrush AxisBrush = Brush("#324149");
    private static readonly IBrush StrongAxisBrush = Brush("#60747D");
    private static readonly IBrush AxisLabelBrush = Brush("#829097");
    private static readonly IBrush LabelSurfaceBrush = Brush("#EE101619");
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
    private bool strongGrid;

    public ReplayRenderMode Mode { get; private set; } = ReplayRenderMode.Compare;
    public double ZoomFactor => zoomFactor;
    public bool StrongGrid => strongGrid;

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

    public void SetStrongGrid(bool value)
    {
        strongGrid = value;
        InvalidateVisual();
    }

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

        const double horizontalMargin = 58;
        const double topMargin = 34;
        const double bottomMargin = 44;
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
        DrawGrid(context, viewport, strongGrid);
        DrawAxisLabels(context, viewport, scene);

        DrawTrails(context, viewport, scene);

        if (Mode is ReplayRenderMode.ReportedFrame or ReplayRenderMode.Compare)
        {
            foreach (var contact in scene.ReportedContacts)
                DrawContact(context, viewport, contact, scene, reported: true);
        }
        if (Mode is ReplayRenderMode.HostState or ReplayRenderMode.Compare)
        {
            foreach (var contact in scene.HostContacts)
                DrawContact(context, viewport, contact, scene, reported: false);
        }

        var labels = (Mode == ReplayRenderMode.HostState
            ? scene.HostContacts
            : scene.ReportedContacts.Concat(scene.HostContacts).GroupBy(contact => contact.Id).Select(group => group.First()))
            .OrderBy(contact => contact.Id)
            .ToArray();
        var protectedPoints = labels
            .Select(contact => Project(viewport, scene, contact.X, contact.Y))
            .Select(center => new Rect(center.X - 23, center.Y - 23, 46, 46))
            .ToArray();
        var occupiedLabels = new List<Rect>();
        foreach (var contact in labels)
            DrawContactLabel(context, viewport, available, contact, scene, protectedPoints, occupiedLabels);

        DrawLegend(context, viewport, scene);

        if (scene.GlobalPalm)
            context.DrawRectangle(null, new Pen(AlarmBrush, 4), viewport);
    }

    private static void DrawGrid(DrawingContext context, Rect viewport, bool strong)
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
                    Project(viewport, scene, trail.Points[index - 1].X, trail.Points[index - 1].Y),
                    Project(viewport, scene, trail.Points[index].X, trail.Points[index].Y));
            }
        }
    }

    private static void DrawLegend(DrawingContext context, Rect viewport, ReplayScene scene)
    {
        var entries = scene.ReportedContacts
            .Concat(scene.HostContacts)
            .Select(contact => (contact.Id, contact.Type))
            .Concat(scene.ContactTrails.Select(trail => (trail.Id, trail.Type)))
            .GroupBy(entry => entry.Id)
            .Select(group => group.First())
            .OrderBy(entry => entry.Id)
            .Take(10)
            .ToArray();
        if (entries.Length == 0) return;

        const double itemHeight = 19;
        var legendRect = new Rect(
            viewport.Left + 12,
            viewport.Top + 12,
            112,
            25 + (entries.Length * itemHeight));
        context.DrawRectangle(LabelSurfaceBrush, new Pen(AxisBrush, 1), legendRect, 3, 3);

        var heading = new FormattedText(
            "CONTACTS",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            8,
            AxisLabelBrush);
        context.DrawText(heading, new Point(legendRect.X + 10, legendRect.Y + 6));

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var origin = new Point(
                legendRect.X + 10,
                legendRect.Y + 21 + (index * itemHeight));
            var color = ContactColor(entry.Id);
            var brush = new SolidColorBrush(color);
            context.DrawEllipse(brush, null, new Point(origin.X + 4, origin.Y + 7), 3.5, 3.5);
            var text = new FormattedText(
                $"#{entry.Id}",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.SemiBold),
                9,
                LabelTextBrush);
            context.DrawText(text, new Point(origin.X + 11, origin.Y));
            DrawLegendType(context, new Point(origin.X + 42, origin.Y + 1), entry.Type, TypeLabel(entry.Type));
        }
    }

    private static string TypeLabel(TouchType type) => type switch
    {
        TouchType.Finger => "finger",
        TouchType.Glove => "glove",
        TouchType.Palm => "palm",
        _ => "other",
    };

    private static void DrawLegendType(DrawingContext context, Point origin, TouchType type, string label)
    {
        DrawTypeIcon(context, type, new Point(origin.X + 4, origin.Y + 6), AxisLabelBrush, 3.5);
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.Medium),
            8,
            AxisLabelBrush);
        context.DrawText(text, new Point(origin.X + 11, origin.Y));
    }

    private static void DrawContact(
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

    private static void DrawContactLabel(
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
            $"#{contact.Id}  {state}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            10,
            LabelTextBrush);
        var coordinateText = new FormattedText(
            $"X {contact.X}  ·  Y {contact.Y}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Medium),
            9,
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

    private static void DrawAxisLabels(DrawingContext context, Rect viewport, ReplayScene scene)
    {
        var tickPen = new Pen(AxisBrush, 1);
        for (var tick = 0; tick <= 4; tick++)
        {
            var fraction = tick / 4d;
            var x = viewport.Left + (viewport.Width * fraction);
            var y = viewport.Top + (viewport.Height * fraction);
            var xValue = scene.Extent.MaximumX * (scene.ReverseX ? 1 - fraction : fraction);
            var yValue = scene.Extent.MaximumY * (scene.ReverseY ? 1 - fraction : fraction);
            context.DrawLine(tickPen, new Point(x, viewport.Bottom), new Point(x, viewport.Bottom + 5));
            context.DrawLine(tickPen, new Point(viewport.Left - 5, y), new Point(viewport.Left, y));
            DrawAxisText(context, xValue.ToString("0", CultureInfo.InvariantCulture), new Point(x, viewport.Bottom + 8), centered: true);
            DrawAxisText(context, yValue.ToString("0", CultureInfo.InvariantCulture), new Point(viewport.Left - 9, y), rightAligned: true);
        }
        DrawAxisText(context, "X", new Point(viewport.Right + 10, viewport.Bottom + 8));
        DrawAxisText(context, "Y", new Point(viewport.Left - 9, viewport.Top - 19), rightAligned: true);
    }

    private static void DrawAxisText(
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
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Medium),
            9,
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
        var pen = new Pen(brush, 1.2);
        switch (type)
        {
            case TouchType.Finger:
                context.DrawEllipse(null, pen, center, radius, radius);
                break;
            case TouchType.Glove:
                context.DrawLine(pen, new Point(center.X, center.Y - radius), new Point(center.X + radius, center.Y));
                context.DrawLine(pen, new Point(center.X + radius, center.Y), new Point(center.X, center.Y + radius));
                context.DrawLine(pen, new Point(center.X, center.Y + radius), new Point(center.X - radius, center.Y));
                context.DrawLine(pen, new Point(center.X - radius, center.Y), new Point(center.X, center.Y - radius));
                break;
            case TouchType.Palm:
                context.DrawRectangle(null, pen, new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
                break;
            default:
                context.DrawLine(pen, center - new Vector(radius, radius), center + new Vector(radius, radius));
                context.DrawLine(pen, center + new Vector(radius, -radius), center + new Vector(-radius, radius));
                break;
        }
    }

    private static Color ContactColor(byte id) => ContactColors[(Math.Max(1, (int)id) - 1) % ContactColors.Length];

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));
}
