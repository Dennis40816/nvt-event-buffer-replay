using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public static class ReplayFrameRenderer
{
    private static readonly Rgb Background = new(10, 13, 15);
    private static readonly Rgb Panel = new(20, 24, 27);
    private static readonly Rgb Grid = new(30, 40, 45);
    private static readonly Rgb Host = new(200, 243, 108);
    private static readonly Rgb Reported = new(88, 199, 217);
    private static readonly Rgb Break = new(226, 166, 91);
    private static readonly Rgb Palm = new(212, 119, 184);
    private static readonly Rgb Alarm = new(221, 102, 94);
    private static readonly Rgb Text = new(222, 229, 226);
    private static readonly Rgb[] ContactColors =
    [
        new(103, 213, 228),
        new(200, 243, 108),
        new(255, 190, 107),
        new(187, 162, 255),
        new(255, 127, 145),
        new(120, 169, 255),
        new(102, 211, 154),
        new(228, 140, 224),
        new(242, 221, 112),
        new(118, 215, 183),
    ];

    public static byte[] RenderRgb(ReplayScene scene, int width, int height, ReplayRenderMode mode = ReplayRenderMode.Compare)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (width < 320 || height < 180) throw new ArgumentOutOfRangeException(nameof(width), "Replay frames must be at least 320x180.");
        var canvas = new RasterCanvas(width, height, Background);
        var top = Math.Max(34, height / 14);
        var bottom = Math.Max(30, height / 16);
        canvas.FillRectangle(0, 0, width, top, Panel);
        canvas.FillRectangle(0, height - bottom, width, bottom, Panel);
        var margin = Math.Max(20, width / 32);
        var viewport = new PixelRect(margin, top + 12, width - (margin * 2), height - top - bottom - 24);
        canvas.Rectangle(viewport, Grid, 1);
        for (var division = 1; division < 8; division++)
        {
            var x = viewport.X + (viewport.Width * division / 8);
            var y = viewport.Y + (viewport.Height * division / 8);
            canvas.Line(x, viewport.Y, x, viewport.Bottom, Grid);
            canvas.Line(viewport.X, y, viewport.Right, y, Grid);
        }

        foreach (var trail in scene.ContactTrails)
            DrawTrail(canvas, viewport, scene.Extent, trail);
        if (mode is ReplayRenderMode.ReportedFrame or ReplayRenderMode.Compare)
            foreach (var contact in scene.ReportedContacts) DrawContact(canvas, viewport, scene.Extent, contact, reported: true);
        if (mode is ReplayRenderMode.HostState or ReplayRenderMode.Compare)
            foreach (var contact in scene.HostContacts) DrawContact(canvas, viewport, scene.Extent, contact, reported: false);
        var labels = mode == ReplayRenderMode.HostState
            ? scene.HostContacts
            : scene.ReportedContacts.Concat(scene.HostContacts).GroupBy(contact => contact.Id).Select(group => group.First());
        foreach (var contact in labels.OrderBy(contact => contact.Id))
            DrawContactLabel(canvas, viewport, scene.Extent, contact, Math.Max(1, height / 540));
        if (scene.GlobalPalm) canvas.Rectangle(viewport, Alarm, 4);

        var scale = Math.Max(1, height / 360);
        canvas.Text(12, Math.Max(7, (top - (5 * scale)) / 2),
            $"FRAME {scene.LogicalIndex + 1}/{scene.TotalFrames}  REC {Clock(scene.Timeline.RecordedTime)}  FRAME {Clock(scene.Timeline.FrameTime)}", Text, scale);
        var alarmCount = scene.Diagnostics.Count(item => item.Severity is DiagnosticSeverity.Alarm or DiagnosticSeverity.Error);
        var warningCount = scene.Diagnostics.Count(item => item.Severity == DiagnosticSeverity.Warning);
        var context = $"TOUCH {scene.ReportedContacts.Count}  ALARM {alarmCount}  WARN {warningCount}  MARK {scene.MarkerLabels.Count}";
        canvas.Text(12, height - bottom + Math.Max(7, (bottom - (5 * scale)) / 2), context, alarmCount > 0 ? Alarm : Text, scale);
        return canvas.Pixels;
    }

    private static void DrawContact(RasterCanvas canvas, PixelRect viewport, ReplayExtent extent, ReplayContact contact, bool reported)
    {
        var x = viewport.X + (int)Math.Round(contact.X / extent.MaximumX * viewport.Width);
        var y = viewport.Y + (int)Math.Round(contact.Y / extent.MaximumY * viewport.Height);
        var color = contact.Invalid ? Alarm : contact.Type == TouchType.Palm ? Palm : contact.Status == TouchStatus.Break ? Break : ContactColor(contact.Id);
        var radius = reported ? 10 : 6;
        if (contact.Invalid)
        {
            canvas.Line(x - radius, y - radius, x + radius, y + radius, color, 2);
            canvas.Line(x + radius, y - radius, x - radius, y + radius, color, 2);
        }
        else if (contact.Status == TouchStatus.Break)
        {
            canvas.Line(x - radius, y - radius, x + radius, y + radius, color, 2);
            canvas.Line(x + radius, y - radius, x - radius, y + radius, color, 2);
        }
        else if (contact.Type == TouchType.Palm)
        {
            var rectangle = new PixelRect(x - radius, y - radius, radius * 2, radius * 2);
            if (!reported) canvas.FillRectangle(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, color);
            canvas.Rectangle(rectangle, color, reported ? 2 : 1);
        }
        else if (reported)
        {
            canvas.Circle(x, y, radius, color, 2);
            if (contact.Status == TouchStatus.Enter) canvas.Circle(x, y, radius + 5, Fade(color), 1);
        }
        else
        {
            canvas.FillCircle(x, y, radius, color);
        }
    }

    private static void DrawTrail(RasterCanvas canvas, PixelRect viewport, ReplayExtent extent, ReplayContactTrail trail)
    {
        var color = ContactColor(trail.Id);
        for (var index = 1; index < trail.Points.Count; index++)
        {
            var prior = trail.Points[index - 1];
            var current = trail.Points[index];
            canvas.Line(
                viewport.X + (int)Math.Round(prior.X / extent.MaximumX * viewport.Width),
                viewport.Y + (int)Math.Round(prior.Y / extent.MaximumY * viewport.Height),
                viewport.X + (int)Math.Round(current.X / extent.MaximumX * viewport.Width),
                viewport.Y + (int)Math.Round(current.Y / extent.MaximumY * viewport.Height),
                color,
                2);
        }
    }

    private static void DrawContactLabel(
        RasterCanvas canvas,
        PixelRect viewport,
        ReplayExtent extent,
        ReplayContact contact,
        int scale)
    {
        var x = viewport.X + (int)Math.Round(contact.X / extent.MaximumX * viewport.Width);
        var y = viewport.Y + (int)Math.Round(contact.Y / extent.MaximumY * viewport.Height);
        var state = contact.Status switch
        {
            TouchStatus.Enter => "ENTER",
            TouchStatus.Move => "MOVE",
            TouchStatus.Break => "BREAK",
            _ => contact.Type == TouchType.Palm ? "PALM" : "IDLE",
        };
        var label = $"#{contact.Id} {state}";
        var width = ((label.Length * 4) + 2) * scale;
        var height = 7 * scale;
        var left = Math.Clamp(x + (14 * scale), viewport.X + 2, viewport.Right - width - 2);
        var top = Math.Clamp(y - (height / 2), viewport.Y + 2, viewport.Bottom - height - 2);
        canvas.FillRectangle(left, top, width, height, Panel);
        canvas.Rectangle(new PixelRect(left, top, width, height), ContactColor(contact.Id), 1);
        canvas.Text(left + scale, top + scale, label, Text, scale);
    }

    private static Rgb ContactColor(byte id) => ContactColors[(Math.Max(1, (int)id) - 1) % ContactColors.Length];

    private static Rgb Fade(Rgb color) => new((byte)(color.Red / 2), (byte)(color.Green / 2), (byte)(color.Blue / 2));

    private static string Clock(TimeSpan value) => $"{(int)value.TotalMinutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";

    private readonly record struct Rgb(byte Red, byte Green, byte Blue);
    private readonly record struct PixelRect(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    private sealed class RasterCanvas
    {
        private static readonly IReadOnlyDictionary<char, string> Font = new Dictionary<char, string>
        {
            [' '] = "000000000000000", ['-'] = "000000111000000", ['.'] = "000000000000010", [':'] = "000010000010000",
            ['/'] = "001001010100100", ['#'] = "010111010111010", ['='] = "000111000111000",
            ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "110001111100111", ['3'] = "110001110001110",
            ['4'] = "101101111001001", ['5'] = "111100110001110", ['6'] = "011100111101111", ['7'] = "111001010010010",
            ['8'] = "111101111101111", ['9'] = "111101111001110",
            ['A'] = "010101111101101", ['B'] = "110101110101110", ['C'] = "011100100100011", ['D'] = "110101101101110",
            ['E'] = "111100110100111", ['F'] = "111100110100100", ['G'] = "011100101101011", ['H'] = "101101111101101",
            ['I'] = "111010010010111", ['J'] = "001001001101010", ['K'] = "101101110101101", ['L'] = "100100100100111",
            ['M'] = "101111111101101", ['N'] = "101111111111101", ['O'] = "010101101101010", ['P'] = "110101110100100",
            ['Q'] = "010101101111011", ['R'] = "110101110101101", ['S'] = "011100010001110", ['T'] = "111010010010010",
            ['U'] = "101101101101111", ['V'] = "101101101101010", ['W'] = "101101111111101", ['X'] = "101101010101101",
            ['Y'] = "101101010010010", ['Z'] = "111001010100111",
        };

        public RasterCanvas(int width, int height, Rgb background)
        {
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 3];
            FillRectangle(0, 0, width, height, background);
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Pixels { get; }

        public void FillRectangle(int x, int y, int width, int height, Rgb color)
        {
            for (var py = Math.Max(0, y); py < Math.Min(Height, y + height); py++)
                for (var px = Math.Max(0, x); px < Math.Min(Width, x + width); px++) Set(px, py, color);
        }

        public void Rectangle(PixelRect rectangle, Rgb color, int thickness)
        {
            for (var offset = 0; offset < thickness; offset++)
            {
                Line(rectangle.X + offset, rectangle.Y + offset, rectangle.Right - offset, rectangle.Y + offset, color);
                Line(rectangle.X + offset, rectangle.Bottom - offset, rectangle.Right - offset, rectangle.Bottom - offset, color);
                Line(rectangle.X + offset, rectangle.Y + offset, rectangle.X + offset, rectangle.Bottom - offset, color);
                Line(rectangle.Right - offset, rectangle.Y + offset, rectangle.Right - offset, rectangle.Bottom - offset, color);
            }
        }

        public void Line(int x0, int y0, int x1, int y1, Rgb color, int thickness = 1)
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

        public void FillCircle(int centerX, int centerY, int radius, Rgb color)
        {
            for (var y = -radius; y <= radius; y++)
                for (var x = -radius; x <= radius; x++)
                    if ((x * x) + (y * y) <= radius * radius) Set(centerX + x, centerY + y, color);
        }

        public void Circle(int centerX, int centerY, int radius, Rgb color, int thickness)
        {
            var outer = radius * radius;
            var inner = (radius - thickness) * (radius - thickness);
            for (var y = -radius; y <= radius; y++)
                for (var x = -radius; x <= radius; x++)
                {
                    var distance = (x * x) + (y * y);
                    if (distance <= outer && distance >= inner) Set(centerX + x, centerY + y, color);
                }
        }

        public void Text(int x, int y, string value, Rgb color, int scale)
        {
            foreach (var character in value.ToUpperInvariant())
            {
                var glyph = Font.GetValueOrDefault(character, Font[' ']);
                for (var row = 0; row < 5; row++)
                    for (var column = 0; column < 3; column++)
                        if (glyph[(row * 3) + column] == '1') FillRectangle(x + (column * scale), y + (row * scale), scale, scale, color);
                x += 4 * scale;
            }
        }

        private void Set(int x, int y, Rgb color)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            var offset = ((y * Width) + x) * 3;
            Pixels[offset] = color.Red;
            Pixels[offset + 1] = color.Green;
            Pixels[offset + 2] = color.Blue;
        }
    }
}
