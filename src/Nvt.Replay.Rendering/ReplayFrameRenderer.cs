using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using SkiaSharp;

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
        ValidateArguments(scene, width, height, settings);
        var pixels = new byte[checked(width * height * 3)];
        RenderRgbCore(scene, width, height, settings, pixels);
        return pixels;
    }

    public static void RenderRgb(
        ReplayScene scene,
        int width,
        int height,
        ReplayRenderSettings settings,
        byte[] destination)
    {
        ValidateArguments(scene, width, height, settings);
        ArgumentNullException.ThrowIfNull(destination);
        var requiredLength = checked(width * height * 3);
        if (destination.Length != requiredLength)
            throw new ArgumentException($"Destination must contain exactly {requiredLength:N0} bytes.", nameof(destination));

        RenderRgbCore(scene, width, height, settings, destination);
    }

    private static void ValidateArguments(ReplayScene scene, int width, int height, ReplayRenderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);
        if (width < 320 || height < 180)
            throw new ArgumentOutOfRangeException(nameof(width), "Replay frames must be at least 320x180.");
    }

    private static void RenderRgbCore(
        ReplayScene scene,
        int width,
        int height,
        ReplayRenderSettings settings,
        byte[] pixels)
    {
        var palette = ReplayVisualStyle.For(settings.Theme);
        using var canvas = new RasterCanvas(width, height, palette.Stage);
        var chromeScale = Math.Min(width / 1280d, height / 720d);
        var top = Math.Max(26, (int)Math.Round(48 * chromeScale));
        var bottom = Math.Max(24, (int)Math.Round(42 * chromeScale));
        var contentBounds = new PixelRect(0, top, width, height - top - bottom);
        var viewport = BuildViewport(scene.ViewExtent, contentBounds, chromeScale);

        DrawChrome(canvas, scene, settings, palette, top, bottom);
        canvas.FillRectangle(viewport.X, viewport.Y, viewport.Width, viewport.Height, palette.Surface);
        DrawGrid(canvas, viewport, settings.StrongGrid, palette);
        DrawAxisLabels(canvas, viewport, scene, palette, TextScale(height));
        DrawScene(canvas, viewport, contentBounds, scene, settings.Mode, palette, chromeScale);
        DrawLegend(canvas, viewport, scene, settings, palette);
        canvas.CopyTo(pixels);
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
            var xValue = scene.ViewExtent.MaximumX * (scene.ReverseX ? 1 - fraction : fraction);
            var yValue = scene.ViewExtent.MaximumY * (scene.ReverseY ? 1 - fraction : fraction);
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
        var labelData = new RasterLabelData[contacts.Count];
        var requests = new ReplayLabelRequest[contacts.Count];
        for (var index = 0; index < contacts.Count; index++)
        {
            var contact = contacts[index];
            var center = Project(viewport, scene, contact.X, contact.Y);
            var header = $"ID {contact.Id}  {ContactState(contact)}";
            var viewCoordinates = scene.ViewCoordinates(contact.X, contact.Y);
            var coordinates = $"X {viewCoordinates.X:0}  Y {viewCoordinates.Y:0}  {TypeLabel(contact.Type)}";
            var iconReserve = 13 * textScale;
            var padding = 5 * textScale;
            var lineGap = Math.Max(2, textScale);
            var width = Math.Max(canvas.TextWidth(header, textScale) + iconReserve, canvas.TextWidth(coordinates, textScale)) + (padding * 2);
            var height = (canvas.TextHeight(textScale) * 2) + lineGap + (padding * 2);
            labelData[index] = new RasterLabelData(contact, center, header, coordinates, iconReserve, padding, lineGap, width, height);
            requests[index] = new ReplayLabelRequest(contact.Id, center.X, center.Y, width, height);
        }

        var placements = ReplayLabelLayout.Place(
                new ReplayLabelBounds(labelBounds.X + 4, labelBounds.Y + 4, labelBounds.Width - 8, labelBounds.Height - 8),
                requests,
                pointRadius,
                Math.Max(20, (int)Math.Round(27 * chromeScale)),
                Math.Max(4, (int)Math.Round(5 * chromeScale)));

        for (var index = 0; index < labelData.Length; index++)
        {
            var data = labelData[index];
            var contact = data.Contact;
            var placement = placements[index];
            var label = new PixelRect(
                (int)Math.Round(placement.Bounds.X),
                (int)Math.Round(placement.Bounds.Y),
                (int)Math.Round(placement.Bounds.Width),
                (int)Math.Round(placement.Bounds.Height));

            var color = ReplayVisualStyle.ContactColor(palette, contact.Id);
            canvas.Line(
                data.Center.X,
                data.Center.Y,
                (int)Math.Round(placement.LeaderX),
                (int)Math.Round(placement.LeaderY),
                color);
            canvas.FillRectangle(label.X, label.Y, label.Width, label.Height, palette.LabelSurface);
            canvas.Rectangle(label, color, 1);
            var headerY = label.Y + data.Padding;
            DrawTypeIcon(canvas, contact.Type, new PixelPoint(label.X + data.Padding + (4 * textScale), headerY + (canvas.TextHeight(textScale) / 2)), color, Math.Max(3, 3 * textScale));
            canvas.Text(label.X + data.Padding + data.IconReserve, headerY, data.Header, palette.LabelText, textScale);
            canvas.Text(label.X + data.Padding, headerY + canvas.TextHeight(textScale) + data.LineGap, data.Coordinates, palette.AxisLabel, textScale);
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
        var viewCoordinates = scene.ViewCoordinates(x, y);
        var xRatio = viewCoordinates.X / scene.ViewExtent.MaximumX;
        var yRatio = viewCoordinates.Y / scene.ViewExtent.MaximumY;
        return new PixelPoint(
            viewport.X + (int)Math.Round((scene.ReverseX ? 1 - xRatio : xRatio) * viewport.Width),
            viewport.Y + (int)Math.Round((scene.ReverseY ? 1 - yRatio : yRatio) * viewport.Height));
    }

    private static ReplayColor Fade(ReplayColor color) =>
        new((byte)(color.Red / 2), (byte)(color.Green / 2), (byte)(color.Blue / 2));

    private static int TextScale(int height) => Math.Max(1, (int)Math.Round(height / 360d));

    private static string Clock(TimeSpan value) =>
        $"{(int)value.TotalMinutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";

    private readonly record struct PixelPoint(int X, int Y);
    private readonly record struct PixelSize(int Width, int Height);
    private readonly record struct RasterLabelData(
        ReplayContact Contact,
        PixelPoint Center,
        string Header,
        string Coordinates,
        int IconReserve,
        int Padding,
        int LineGap,
        int Width,
        int Height);
    private readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    private sealed class RasterCanvas : IDisposable
    {
        private static readonly SKTypeface Typeface =
            SKTypeface.FromFamilyName("Cascadia Mono", SKFontStyle.Bold) ?? SKTypeface.Default;
        private readonly SKBitmap bitmap;
        private readonly SKCanvas canvas;

        public RasterCanvas(int width, int height, ReplayColor background)
        {
            Width = width;
            Height = height;
            bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            canvas = new SKCanvas(bitmap);
            canvas.Clear(ToSkColor(background));
        }

        public int Width { get; }
        public int Height { get; }

        public int TextWidth(string value, int scale)
        {
            using var font = CreateFont(scale);
            using var paint = CreatePaint(default, SKPaintStyle.Fill);
            return Math.Max(0, (int)Math.Ceiling(font.MeasureText(value, paint)));
        }

        public int TextHeight(int scale)
        {
            using var font = CreateFont(scale);
            var metrics = font.Metrics;
            return Math.Max(1, (int)Math.Ceiling(metrics.Descent - metrics.Ascent));
        }

        public void FillRectangle(int x, int y, int width, int height, ReplayColor color)
        {
            using var paint = CreatePaint(color, SKPaintStyle.Fill);
            canvas.DrawRect(x, y, width, height, paint);
        }

        public void FillRoundedRectangle(PixelRect rectangle, int radius, ReplayColor color)
        {
            radius = Math.Clamp(radius, 0, Math.Min(rectangle.Width, rectangle.Height) / 2);
            using var paint = CreatePaint(color, SKPaintStyle.Fill);
            canvas.DrawRoundRect(ToSkRect(rectangle), radius, radius, paint);
        }

        public void Rectangle(PixelRect rectangle, ReplayColor color, int thickness)
        {
            using var paint = CreatePaint(color, SKPaintStyle.Stroke, thickness);
            canvas.DrawRect(ToSkRect(rectangle), paint);
        }

        public void RoundedRectangle(PixelRect rectangle, int radius, ReplayColor color, int thickness)
        {
            using var paint = CreatePaint(color, SKPaintStyle.Stroke, thickness);
            canvas.DrawRoundRect(ToSkRect(rectangle), radius, radius, paint);
        }

        public void Line(int x0, int y0, int x1, int y1, ReplayColor color, int thickness = 1)
        {
            using var paint = CreatePaint(color, SKPaintStyle.Stroke, thickness);
            paint.StrokeCap = SKStrokeCap.Round;
            canvas.DrawLine(x0, y0, x1, y1, paint);
        }

        public void FillCircle(int centerX, int centerY, int radius, ReplayColor color)
        {
            using var paint = CreatePaint(color, SKPaintStyle.Fill);
            canvas.DrawCircle(centerX, centerY, radius, paint);
        }

        public void Circle(int centerX, int centerY, int radius, ReplayColor color, int thickness)
        {
            using var paint = CreatePaint(color, SKPaintStyle.Stroke, thickness);
            canvas.DrawCircle(centerX, centerY, radius, paint);
        }

        public void FillPolygon(ReplayColor color, params PixelPoint[] points)
        {
            if (points.Length < 3) return;
            using var path = new SKPath();
            path.MoveTo(points[0].X, points[0].Y);
            foreach (var point in points.Skip(1)) path.LineTo(point.X, point.Y);
            path.Close();
            using var paint = CreatePaint(color, SKPaintStyle.Fill);
            canvas.DrawPath(path, paint);
        }

        public void Text(int x, int y, string value, ReplayColor color, int scale)
        {
            using var font = CreateFont(scale);
            using var paint = CreatePaint(color, SKPaintStyle.Fill);
            var metrics = font.Metrics;
            canvas.DrawText(value, x, y - metrics.Ascent, font, paint);
        }

        public void TextCentered(int centerX, int y, string value, ReplayColor color, int scale) =>
            Text(centerX - (TextWidth(value, scale) / 2), y, value, color, scale);

        public void TextRight(int right, int y, string value, ReplayColor color, int scale) =>
            Text(right - TextWidth(value, scale), y, value, color, scale);

        public void CopyTo(byte[] destination)
        {
            canvas.Flush();
            var source = bitmap.GetPixelSpan();
            for (int sourceOffset = 0, destinationOffset = 0;
                 destinationOffset < destination.Length;
                 sourceOffset += 4, destinationOffset += 3)
            {
                destination[destinationOffset] = source[sourceOffset];
                destination[destinationOffset + 1] = source[sourceOffset + 1];
                destination[destinationOffset + 2] = source[sourceOffset + 2];
            }
        }

        public void Dispose()
        {
            canvas.Dispose();
            bitmap.Dispose();
        }

        private static SKFont CreateFont(int scale) => new(Typeface, Math.Max(8, 6f * scale));

        private static SKPaint CreatePaint(ReplayColor color, SKPaintStyle style, float strokeWidth = 1) => new()
        {
            Color = ToSkColor(color),
            IsAntialias = true,
            Style = style,
            StrokeWidth = strokeWidth,
            StrokeJoin = SKStrokeJoin.Round,
        };

        private static SKRect ToSkRect(PixelRect rectangle) =>
            new(rectangle.X, rectangle.Y, rectangle.Right, rectangle.Bottom);

        private static SKColor ToSkColor(ReplayColor color) =>
            new(color.Red, color.Green, color.Blue, byte.MaxValue);
    }
}
