using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public static class ReplayFrameRenderer
{
    public static byte[] RenderRgb(
        ReplayScene scene,
        int width,
        int height,
        ReplayRenderMode mode = ReplayRenderMode.Compare) =>
        RenderRgb(scene, width, height, new ReplayRenderSettings(mode));

    public static byte[] RenderRgb(
        ReplayScene scene,
        int width,
        int height,
        ReplayRenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);
        if (width < 320 || height < 180)
            throw new ArgumentOutOfRangeException(nameof(width), "Replay frames must be at least 320x180.");

        var palette = ReplayVisualStyle.For(settings.Theme);
        var canvas = new RasterCanvas(width, height, palette.Stage);
        var chromeScale = Math.Min(width / 1280d, height / 720d);
        var top = Math.Max(26, (int)Math.Round(48 * chromeScale));
        var bottom = Math.Max(24, (int)Math.Round(42 * chromeScale));
        var contentBounds = new PixelRect(0, top, width, height - top - bottom);
        var viewport = BuildViewport(scene.Extent, contentBounds, chromeScale);

        DrawChrome(canvas, scene, settings, palette, top, bottom);
        canvas.FillRectangle(viewport.X, viewport.Y, viewport.Width, viewport.Height, palette.Surface);
        DrawGrid(canvas, viewport, settings.StrongGrid, palette);
        DrawAxisLabels(canvas, viewport, scene, palette, TextScale(height));
        DrawScene(canvas, viewport, contentBounds, scene, settings.Mode, palette, chromeScale);
        DrawLegend(canvas, viewport, scene, settings, palette);
        return canvas.Pixels;
    }

    private static PixelRect BuildViewport(ReplayExtent extent, PixelRect bounds, double chromeScale)
    {
        var horizontalMargin = Math.Max(22, (int)Math.Round(48 * chromeScale));
        var topMargin = Math.Max(12, (int)Math.Round(24 * chromeScale));
        var bottomMargin = Math.Max(20, (int)Math.Round(38 * chromeScale));
        var available = new PixelRect(
            bounds.X + horizontalMargin,
            bounds.Y + topMargin,
            Math.Max(1, bounds.Width - (horizontalMargin * 2)),
            Math.Max(1, bounds.Height - topMargin - bottomMargin));
        var fitScale = Math.Min(available.Width / extent.MaximumX, available.Height / extent.MaximumY);
        var viewportWidth = Math.Max(1, (int)Math.Round(extent.MaximumX * fitScale));
        var viewportHeight = Math.Max(1, (int)Math.Round(extent.MaximumY * fitScale));
        return new PixelRect(
            available.X + ((available.Width - viewportWidth) / 2),
            available.Y + ((available.Height - viewportHeight) / 2),
            viewportWidth,
            viewportHeight);
    }

    private static void DrawChrome(
        RasterCanvas canvas,
        ReplayScene scene,
        ReplayRenderSettings settings,
        ReplayVisualPalette palette,
        int top,
        int bottom)
    {
        var scale = TextScale(canvas.Height);
        canvas.Line(0, top - 1, canvas.Width, top - 1, palette.Axis);
        canvas.Line(0, canvas.Height - bottom, canvas.Width, canvas.Height - bottom, palette.Axis);
        var header = $"FRAME {scene.LogicalIndex + 1}/{scene.TotalFrames}  REC {Clock(scene.Timeline.RecordedTime)}  TP {Clock(scene.Timeline.FrameTime)}";
        canvas.Text(14, Math.Max(6, (top - canvas.TextHeight(scale)) / 2), header, palette.LabelText, scale);

        var alarmCount = scene.Diagnostics.Count(item => item.Severity is DiagnosticSeverity.Alarm or DiagnosticSeverity.Error);
        var warningCount = scene.Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning);
        var active = settings.Mode == ReplayRenderMode.ReportedFrame ? scene.ReportedContacts : scene.HostContacts;
        var footer = $"CONTACT {active.Count}  ALARM {alarmCount}  WARN {warningCount}  MARK {scene.MarkerLabels.Count}";
        canvas.Text(
            14,
            canvas.Height - bottom + Math.Max(6, (bottom - canvas.TextHeight(scale)) / 2),
            footer,
            alarmCount > 0 ? palette.Alarm : palette.AxisLabel,
            scale);
    }

    private static void DrawGrid(RasterCanvas canvas, PixelRect viewport, bool strong, ReplayVisualPalette palette)
    {
        var grid = strong ? palette.StrongGrid : palette.Grid;
        var axis = strong ? palette.StrongAxis : palette.Axis;
        var gridThickness = strong ? 2 : 1;
        canvas.Rectangle(viewport, axis, strong ? 2 : 1);
        for (var division = 1; division < 8; division++)
        {
            var x = viewport.X + (viewport.Width * division / 8);
            var y = viewport.Y + (viewport.Height * division / 8);
            canvas.Line(x, viewport.Y, x, viewport.Bottom, grid, gridThickness);
            canvas.Line(viewport.X, y, viewport.Right, y, grid, gridThickness);
        }
    }

    private static void DrawAxisLabels(
        RasterCanvas canvas,
        PixelRect viewport,
        ReplayScene scene,
        ReplayVisualPalette palette,
        int textScale)
    {
        for (var tick = 0; tick <= 4; tick++)
        {
            var fraction = tick / 4d;
            var x = viewport.X + (int)Math.Round(viewport.Width * fraction);
            var y = viewport.Y + (int)Math.Round(viewport.Height * fraction);
            var xValue = scene.Extent.MaximumX * (scene.ReverseX ? 1 - fraction : fraction);
            var yValue = scene.Extent.MaximumY * (scene.ReverseY ? 1 - fraction : fraction);
            canvas.Line(x, viewport.Bottom, x, viewport.Bottom + 5, palette.StrongAxis);
            canvas.Line(viewport.X - 5, y, viewport.X, y, palette.StrongAxis);
            canvas.TextCentered(x, viewport.Bottom + 8, xValue.ToString("0"), palette.AxisLabel, textScale);
            canvas.TextRight(viewport.X - 9, y - (canvas.TextHeight(textScale) / 2), yValue.ToString("0"), palette.AxisLabel, textScale);
        }
        canvas.Text(viewport.Right + 20, viewport.Bottom + 8, "X", palette.AxisLabel, textScale);
        canvas.TextRight(viewport.X - 9, viewport.Y - canvas.TextHeight(textScale) - 7, "Y", palette.AxisLabel, textScale);
    }

    private static void DrawScene(
        RasterCanvas canvas,
        PixelRect viewport,
        PixelRect contentBounds,
        ReplayScene scene,
        ReplayRenderMode mode,
        ReplayVisualPalette palette,
        double chromeScale)
    {
        foreach (var trail in scene.ContactTrails)
            DrawTrail(canvas, viewport, scene, trail, palette, Math.Max(2, (int)Math.Round(2.5 * chromeScale)));

        if (mode is ReplayRenderMode.ReportedFrame or ReplayRenderMode.Compare)
            foreach (var contact in scene.ReportedContacts)
                DrawContact(canvas, viewport, scene, contact, reported: true, palette, chromeScale);
        if (mode is ReplayRenderMode.HostState or ReplayRenderMode.Compare)
            foreach (var contact in scene.HostContacts)
                DrawContact(canvas, viewport, scene, contact, reported: false, palette, chromeScale);

        var labels = (mode == ReplayRenderMode.HostState
                ? scene.HostContacts
                : scene.ReportedContacts.Concat(scene.HostContacts)
                    .GroupBy(contact => contact.Id)
                    .Select(group => group.First()))
            .OrderBy(contact => contact.Id)
            .ToArray();
        DrawContactLabels(canvas, viewport, contentBounds, scene, labels, palette, chromeScale);
        if (scene.GlobalPalm)
            canvas.Rectangle(viewport, palette.Alarm, Math.Max(3, (int)Math.Round(4 * chromeScale)));
    }

    private static void DrawContact(
        RasterCanvas canvas,
        PixelRect viewport,
        ReplayScene scene,
        ReplayContact contact,
        bool reported,
        ReplayVisualPalette palette,
        double chromeScale)
    {
        var center = Project(viewport, scene, contact.X, contact.Y);
        var idColor = ReplayVisualStyle.ContactColor(palette, contact.Id);
        var reportedRadius = Math.Max(10, (int)Math.Round(15 * chromeScale));
        var hostRadius = Math.Max(6, (int)Math.Round(8 * chromeScale));
        var radius = reported ? reportedRadius : hostRadius;
        if (contact.Invalid)
        {
            DrawCross(canvas, center, Math.Max(9, radius), palette.Invalid, Math.Max(2, (int)Math.Round(3 * chromeScale)));
            return;
        }
        if (contact.Status == TouchStatus.Break)
        {
            DrawCross(canvas, center, radius, palette.Break, reported ? 3 : 2);
            return;
        }
        if (contact.Type == TouchType.Palm)
        {
            var rectangle = new PixelRect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
            if (!reported) canvas.FillRectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, palette.Palm);
            canvas.Rectangle(rectangle, palette.Palm, reported ? 3 : 1);
            return;
        }
        if (reported)
        {
            canvas.Circle(center.X, center.Y, radius, idColor, 3);
            if (contact.Status == TouchStatus.Enter)
                canvas.Circle(center.X, center.Y, radius + Math.Max(4, (int)Math.Round(6 * chromeScale)), Fade(idColor), 2);
        }
        else
        {
            canvas.FillCircle(center.X, center.Y, radius, idColor);
            canvas.Circle(center.X, center.Y, radius, palette.Surface, 2);
        }
    }

    private static void DrawCross(RasterCanvas canvas, PixelPoint center, int radius, ReplayColor color, int thickness)
    {
        canvas.Line(center.X - radius, center.Y - radius, center.X + radius, center.Y + radius, color, thickness);
        canvas.Line(center.X + radius, center.Y - radius, center.X - radius, center.Y + radius, color, thickness);
    }

    private static void DrawTrail(
        RasterCanvas canvas,
        PixelRect viewport,
        ReplayScene scene,
        ReplayContactTrail trail,
        ReplayVisualPalette palette,
        int thickness)
    {
        var color = ReplayVisualStyle.ContactColor(palette, trail.Id);
        for (var index = 1; index < trail.Points.Count; index++)
        {
            var prior = Project(viewport, scene, trail.Points[index - 1].X, trail.Points[index - 1].Y);
            var current = Project(viewport, scene, trail.Points[index].X, trail.Points[index].Y);
            canvas.Line(prior.X, prior.Y, current.X, current.Y, color, thickness);
        }
    }

    private static void DrawContactLabels(
        RasterCanvas canvas,
        PixelRect viewport,
        PixelRect labelBounds,
        ReplayScene scene,
        IReadOnlyList<ReplayContact> contacts,
        ReplayVisualPalette palette,
        double chromeScale)
    {
        var textScale = TextScale(canvas.Height);
        var pointRadius = Math.Max(20, (int)Math.Round(23 * chromeScale));
        var protectedPoints = contacts
            .Select(contact => Project(viewport, scene, contact.X, contact.Y))
            .Select(point => new PixelRect(point.X - pointRadius, point.Y - pointRadius, pointRadius * 2, pointRadius * 2))
            .ToArray();
        var occupied = new List<PixelRect>();
        foreach (var contact in contacts)
        {
            var center = Project(viewport, scene, contact.X, contact.Y);
            var header = $"ID {contact.Id}  {ContactState(contact)}";
            var coordinates = $"X {contact.X}  Y {contact.Y}  {TypeLabel(contact.Type)}";
            var iconReserve = 13 * textScale;
            var padding = 5 * textScale;
            var lineGap = Math.Max(2, textScale);
            var width = Math.Max(canvas.TextWidth(header, textScale) + iconReserve, canvas.TextWidth(coordinates, textScale)) + (padding * 2);
            var height = (canvas.TextHeight(textScale) * 2) + lineGap + (padding * 2);
            var clearance = Math.Max(20, (int)Math.Round(27 * chromeScale));
            var origins = new[]
            {
                new PixelPoint(center.X + clearance, center.Y - (height / 2)),
                new PixelPoint(center.X - width - clearance, center.Y - (height / 2)),
                new PixelPoint(center.X - (width / 2), center.Y - height - clearance),
                new PixelPoint(center.X - (width / 2), center.Y + clearance),
                new PixelPoint(center.X + 16, center.Y - height - 16),
                new PixelPoint(center.X - width - 16, center.Y - height - 16),
                new PixelPoint(center.X + 16, center.Y + 16),
                new PixelPoint(center.X - width - 16, center.Y + 16),
            };
            var label = origins
                .Select((origin, priority) =>
                {
                    var candidate = new PixelRect(
                        Math.Clamp(origin.X, labelBounds.X + 4, labelBounds.Right - width - 4),
                        Math.Clamp(origin.Y, labelBounds.Y + 4, labelBounds.Bottom - height - 4),
                        width,
                        height);
                    var score = priority;
                    score += protectedPoints.Sum(point => OverlapPenalty(point, candidate, 100_000));
                    score += occupied.Sum(existing => OverlapPenalty(existing, candidate, 10_000));
                    score += (Math.Abs(candidate.X - origin.X) + Math.Abs(candidate.Y - origin.Y)) * 100;
                    return (candidate, score);
                })
                .OrderBy(result => result.score)
                .First().candidate;
            occupied.Add(label);

            var color = ReplayVisualStyle.ContactColor(palette, contact.Id);
            var leaderEnd = new PixelPoint(
                Math.Clamp(center.X, label.X, label.Right),
                Math.Clamp(center.Y, label.Y, label.Bottom));
            canvas.Line(center.X, center.Y, leaderEnd.X, leaderEnd.Y, color);
            canvas.FillRectangle(label.X, label.Y, label.Width, label.Height, palette.LabelSurface);
            canvas.Rectangle(label, color, 1);
            var headerY = label.Y + padding;
            DrawTypeIcon(canvas, contact.Type, new PixelPoint(label.X + padding + (4 * textScale), headerY + (canvas.TextHeight(textScale) / 2)), color, Math.Max(3, 3 * textScale));
            canvas.Text(label.X + padding + iconReserve, headerY, header, palette.LabelText, textScale);
            canvas.Text(label.X + padding, headerY + canvas.TextHeight(textScale) + lineGap, coordinates, palette.AxisLabel, textScale);
        }
    }

    private static void DrawLegend(
        RasterCanvas canvas,
        PixelRect viewport,
        ReplayScene scene,
        ReplayRenderSettings settings,
        ReplayVisualPalette palette)
    {
        if (!settings.LegendVisible) return;
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

        var active = (settings.Mode switch
        {
            ReplayRenderMode.HostState => scene.HostContacts,
            ReplayRenderMode.ReportedFrame => scene.ReportedContacts,
            _ => scene.ReportedContacts.Concat(scene.HostContacts).ToArray(),
        })
            .Where(contact => contact.IsActive)
            .GroupBy(contact => contact.Id)
            .Select(group => group.First())
            .ToArray();
        var textScale = TextScale(canvas.Height);
        if (settings.LegendCollapsed)
        {
            var summary = ContactSummary(active);
            var height = Math.Max(28, canvas.TextHeight(textScale) + 14);
            var width = canvas.TextWidth(summary, textScale) + 38;
            var rectangle = PositionLegend(viewport, new PixelSize(width, height), settings.LegendPosition, 12);
            canvas.FillRoundedRectangle(rectangle, height / 2, palette.LabelSurface);
            canvas.RoundedRectangle(rectangle, height / 2, palette.Axis, 1);
            canvas.FillCircle(rectangle.X + 13, rectangle.Y + (height / 2), 4, palette.AxisLabel);
            canvas.Text(rectangle.X + 25, rectangle.Y + ((height - canvas.TextHeight(textScale)) / 2), summary, palette.LabelText, textScale);
            return;
        }

        var headerHeight = Math.Max(25, canvas.TextHeight(textScale) + 12);
        var rowHeight = Math.Max(22, canvas.TextHeight(textScale) + 10);
        var widthFull = Math.Max(142, canvas.TextWidth("#10  GLOVE", textScale) + 34);
        var bounds = PositionLegend(
            viewport,
            new PixelSize(widthFull, headerHeight + (entries.Length * rowHeight) + 6),
            settings.LegendPosition,
            12);
        canvas.FillRoundedRectangle(bounds, 4, palette.LabelSurface);
        canvas.RoundedRectangle(bounds, 4, palette.Axis, 1);
        canvas.Text(bounds.X + 10, bounds.Y + ((headerHeight - canvas.TextHeight(textScale)) / 2), "CONTACTS", palette.AxisLabel, textScale);
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var rowY = bounds.Y + headerHeight + (index * rowHeight);
            var color = ReplayVisualStyle.ContactColor(palette, entry.Id);
            canvas.FillCircle(bounds.X + 14, rowY + (rowHeight / 2), 4, color);
            canvas.Text(bounds.X + 24, rowY + ((rowHeight - canvas.TextHeight(textScale)) / 2), $"#{entry.Id}", palette.LabelText, textScale);
            DrawTypeIcon(canvas, entry.Type, new PixelPoint(bounds.X + 72, rowY + (rowHeight / 2)), palette.AxisLabel, Math.Max(3, 3 * textScale));
            canvas.Text(bounds.X + 82, rowY + ((rowHeight - canvas.TextHeight(textScale)) / 2), TypeLabel(entry.Type), palette.AxisLabel, textScale);
        }
    }

    private static PixelRect PositionLegend(PixelRect viewport, PixelSize size, ReplayLegendPosition requested, int inset)
    {
        var position = requested == ReplayLegendPosition.Auto ? ReplayLegendPosition.TopLeft : requested;
        var right = position is ReplayLegendPosition.TopRight or ReplayLegendPosition.BottomRight;
        var bottom = position is ReplayLegendPosition.BottomLeft or ReplayLegendPosition.BottomRight;
        return new PixelRect(
            right ? viewport.Right - inset - size.Width : viewport.X + inset,
            bottom ? viewport.Bottom - inset - size.Height : viewport.Y + inset,
            size.Width,
            size.Height);
    }

    private static string ContactSummary(IReadOnlyCollection<ReplayContact> active)
    {
        var finger = active.Count(contact => contact.Type == TouchType.Finger);
        var glove = active.Count(contact => contact.Type == TouchType.Glove);
        var palm = active.Count(contact => contact.Type == TouchType.Palm);
        var other = active.Count - finger - glove - palm;
        var parts = new List<string>(4);
        AddCount(parts, finger, "FINGER");
        AddCount(parts, glove, "GLOVE");
        AddCount(parts, palm, "PALM");
        AddCount(parts, other, "OTHER");
        return parts.Count == 0 ? "0 CONTACTS" : string.Join("  ", parts);
    }

    private static void AddCount(ICollection<string> parts, int count, string label)
    {
        if (count > 0) parts.Add($"{count} {label}{(count == 1 ? string.Empty : "S")}");
    }

    private static string ContactState(ReplayContact contact) => contact.Status switch
    {
        TouchStatus.Enter => "ENTER",
        TouchStatus.Move => "MOVE",
        TouchStatus.Break => "BREAK",
        _ => contact.Type == TouchType.Palm ? "PALM" : "IDLE",
    };

    private static string TypeLabel(TouchType type) => type switch
    {
        TouchType.Finger => "FINGER",
        TouchType.Glove => "GLOVE",
        TouchType.Palm => "PALM",
        _ => "OTHER",
    };

    private static void DrawTypeIcon(RasterCanvas canvas, TouchType type, PixelPoint center, ReplayColor color, int radius)
    {
        switch (type)
        {
            case TouchType.Finger:
                canvas.FillCircle(center.X, center.Y, radius, color);
                break;
            case TouchType.Glove:
                canvas.FillPolygon(color,
                    new PixelPoint(center.X, center.Y - radius),
                    new PixelPoint(center.X + radius, center.Y),
                    new PixelPoint(center.X, center.Y + radius),
                    new PixelPoint(center.X - radius, center.Y));
                break;
            case TouchType.Palm:
                canvas.FillRectangle(center.X - radius, center.Y - radius, radius * 2, radius * 2, color);
                break;
            default:
                canvas.FillPolygon(color,
                    new PixelPoint(center.X, center.Y - radius),
                    new PixelPoint(center.X + radius, center.Y + radius),
                    new PixelPoint(center.X - radius, center.Y + radius));
                break;
        }
    }

    private static PixelPoint Project(PixelRect viewport, ReplayScene scene, ushort x, ushort y)
    {
        var xRatio = x / scene.Extent.MaximumX;
        var yRatio = y / scene.Extent.MaximumY;
        return new PixelPoint(
            viewport.X + (int)Math.Round((scene.ReverseX ? 1 - xRatio : xRatio) * viewport.Width),
            viewport.Y + (int)Math.Round((scene.ReverseY ? 1 - yRatio : yRatio) * viewport.Height));
    }

    private static int OverlapPenalty(PixelRect first, PixelRect second, int basePenalty)
    {
        if (first.X >= second.Right || first.Right <= second.X || first.Y >= second.Bottom || first.Bottom <= second.Y)
            return 0;
        var width = Math.Min(first.Right, second.Right) - Math.Max(first.X, second.X);
        var height = Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Y, second.Y);
        return basePenalty + (width * height);
    }

    private static ReplayColor Fade(ReplayColor color) =>
        new((byte)(color.Red / 2), (byte)(color.Green / 2), (byte)(color.Blue / 2));

    private static int TextScale(int height) => Math.Max(1, (int)Math.Round(height / 360d));

    private static string Clock(TimeSpan value) =>
        $"{(int)value.TotalMinutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";

    private readonly record struct PixelPoint(int X, int Y);
    private readonly record struct PixelSize(int Width, int Height);
    private readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    private sealed class RasterCanvas
    {
        private static readonly IReadOnlyDictionary<char, string> Font = new Dictionary<char, string>
        {
            [' '] = "000000000000000",
            ['-'] = "000000111000000",
            ['.'] = "000000000000010",
            [':'] = "000010000010000",
            ['/'] = "001001010100100",
            ['#'] = "010111010111010",
            ['='] = "000111000111000",
            ['0'] = "111101101101111",
            ['1'] = "010110010010111",
            ['2'] = "110001111100111",
            ['3'] = "110001110001110",
            ['4'] = "101101111001001",
            ['5'] = "111100110001110",
            ['6'] = "011100111101111",
            ['7'] = "111001010010010",
            ['8'] = "111101111101111",
            ['9'] = "111101111001110",
            ['A'] = "010101111101101",
            ['B'] = "110101110101110",
            ['C'] = "011100100100011",
            ['D'] = "110101101101110",
            ['E'] = "111100110100111",
            ['F'] = "111100110100100",
            ['G'] = "011100101101011",
            ['H'] = "101101111101101",
            ['I'] = "111010010010111",
            ['J'] = "001001001101010",
            ['K'] = "101101110101101",
            ['L'] = "100100100100111",
            ['M'] = "101111111101101",
            ['N'] = "101111111111101",
            ['O'] = "010101101101010",
            ['P'] = "110101110100100",
            ['Q'] = "010101101111011",
            ['R'] = "110101110101101",
            ['S'] = "011100010001110",
            ['T'] = "111010010010010",
            ['U'] = "101101101101111",
            ['V'] = "101101101101010",
            ['W'] = "101101111111101",
            ['X'] = "101101010101101",
            ['Y'] = "101101010010010",
            ['Z'] = "111001010100111",
        };

        public RasterCanvas(int width, int height, ReplayColor background)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 3];
            FillRectangle(0, 0, width, height, background);
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        public int TextWidth(string value, int scale) => Math.Max(0, (value.Length * 4 * scale) - scale);
        public int TextHeight(int scale) => 5 * scale;

        public void FillRectangle(int x, int y, int width, int height, ReplayColor color)
        {
            for (var py = Math.Max(0, y); py < Math.Min(Height, y + height); py++)
                for (var px = Math.Max(0, x); px < Math.Min(Width, x + width); px++) Set(px, py, color);
        }

        public void FillRoundedRectangle(PixelRect rectangle, int radius, ReplayColor color)
        {
            radius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
            FillRectangle(rectangle.X + radius, rectangle.Y, rectangle.Width - (radius * 2), rectangle.Height, color);
            FillRectangle(rectangle.X, rectangle.Y + radius, radius, rectangle.Height - (radius * 2), color);
            FillRectangle(rectangle.Right - radius, rectangle.Y + radius, radius, rectangle.Height - (radius * 2), color);
            FillCircle(rectangle.X + radius, rectangle.Y + radius, radius, color);
            FillCircle(rectangle.Right - radius, rectangle.Y + radius, radius, color);
            FillCircle(rectangle.X + radius, rectangle.Bottom - radius, radius, color);
            FillCircle(rectangle.Right - radius, rectangle.Bottom - radius, radius, color);
        }

        public void Rectangle(PixelRect rectangle, ReplayColor color, int thickness)
        {
            for (var offset = 0; offset < thickness; offset++)
            {
                Line(rectangle.X + offset, rectangle.Y + offset, rectangle.Right - offset, rectangle.Y + offset, color);
                Line(rectangle.X + offset, rectangle.Bottom - offset, rectangle.Right - offset, rectangle.Bottom - offset, color);
                Line(rectangle.X + offset, rectangle.Y + offset, rectangle.X + offset, rectangle.Bottom - offset, color);
                Line(rectangle.Right - offset, rectangle.Y + offset, rectangle.Right - offset, rectangle.Bottom - offset, color);
            }
        }

        public void RoundedRectangle(PixelRect rectangle, int radius, ReplayColor color, int thickness) =>
            Rectangle(rectangle, color, thickness);

        public void Line(int x0, int y0, int x1, int y1, ReplayColor color, int thickness = 1)
        {
            var dx = Math.Abs(x1 - x0);
            var sx = x0 < x1 ? 1 : -1;
            var dy = -Math.Abs(y1 - y0);
            var sy = y0 < y1 ? 1 : -1;
            var error = dx + dy;
            while (true)
            {
                FillRectangle(x0 - (thickness / 2), y0 - (thickness / 2), thickness, thickness, color);
                if (x0 == x1 && y0 == y1) break;
                var twice = 2 * error;
                if (twice >= dy) { error += dy; x0 += sx; }
                if (twice <= dx) { error += dx; y0 += sy; }
            }
        }

        public void FillCircle(int centerX, int centerY, int radius, ReplayColor color)
        {
            for (var y = -radius; y <= radius; y++)
                for (var x = -radius; x <= radius; x++)
                    if ((x * x) + (y * y) <= radius * radius) Set(centerX + x, centerY + y, color);
        }

        public void Circle(int centerX, int centerY, int radius, ReplayColor color, int thickness)
        {
            var outer = radius * radius;
            var innerRadius = Math.Max(0, radius - thickness);
            var inner = innerRadius * innerRadius;
            for (var y = -radius; y <= radius; y++)
                for (var x = -radius; x <= radius; x++)
                {
                    var distance = (x * x) + (y * y);
                    if (distance <= outer && distance >= inner) Set(centerX + x, centerY + y, color);
                }
        }

        public void FillPolygon(ReplayColor color, params PixelPoint[] points)
        {
            if (points.Length < 3) return;
            var minimumY = points.Min(point => point.Y);
            var maximumY = points.Max(point => point.Y);
            for (var y = minimumY; y <= maximumY; y++)
            {
                var intersections = new List<int>();
                for (var index = 0; index < points.Length; index++)
                {
                    var first = points[index];
                    var second = points[(index + 1) % points.Length];
                    if ((first.Y <= y && second.Y > y) || (second.Y <= y && first.Y > y))
                        intersections.Add(first.X + (int)Math.Round((y - first.Y) * (second.X - first.X) / (double)(second.Y - first.Y)));
                }
                intersections.Sort();
                for (var index = 0; index + 1 < intersections.Count; index += 2)
                    Line(intersections[index], y, intersections[index + 1], y, color);
            }
        }

        public void Text(int x, int y, string value, ReplayColor color, int scale)
        {
            foreach (var character in value.ToUpperInvariant())
            {
                var glyph = Font.GetValueOrDefault(character, Font[' ']);
                for (var row = 0; row < 5; row++)
                    for (var column = 0; column < 3; column++)
                        if (glyph[(row * 3) + column] == '1')
                            FillRectangle(x + (column * scale), y + (row * scale), scale, scale, color);
                x += 4 * scale;
            }
        }

        public void TextCentered(int centerX, int y, string value, ReplayColor color, int scale) =>
            Text(centerX - (TextWidth(value, scale) / 2), y, value, color, scale);

        public void TextRight(int right, int y, string value, ReplayColor color, int scale) =>
            Text(right - TextWidth(value, scale), y, value, color, scale);

        private void Set(int x, int y, ReplayColor color)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            var offset = ((y * Width) + x) * 3;
            Pixels[offset] = color.Red;
            Pixels[offset + 1] = color.Green;
            Pixels[offset + 2] = color.Blue;
        }
    }
}
