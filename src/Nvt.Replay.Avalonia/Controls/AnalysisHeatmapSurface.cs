using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Nvt.Replay.Analysis;

namespace Nvt.Replay.Avalonia.Controls;

public sealed record HeatmapHoverCell(
    int Column,
    int Row,
    int Count,
    double PeakRatio,
    double MinimumX,
    double MaximumX,
    double MinimumY,
    double MaximumY);

public sealed class AnalysisHeatmapSurface : Control
{
    private static readonly (double Stop, Color Color)[] Palette =
    [
        (0.00, Color.Parse("#2444A7")),
        (0.25, Color.Parse("#00A6CA")),
        (0.50, Color.Parse("#35B779")),
        (0.75, Color.Parse("#F3DC45")),
        (1.00, Color.Parse("#E43D30")),
    ];
    private static readonly IBrush DarkLabelBrush = new SolidColorBrush(Color.Parse("#071014"));
    private static readonly IBrush LightLabelBrush = new SolidColorBrush(Colors.White);
    private static readonly LinearGradientBrush ColorBarBrush = CreateColorBarBrush();

    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> ToolTipSurfaceBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(ToolTipSurfaceBrush));
    public static readonly StyledProperty<IBrush?> ToolTipBorderBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(ToolTipBorderBrush));

    private AnalysisHotspot? hotspot;
    private HeatmapHoverCell? hoveredCell;

    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? ToolTipSurfaceBrush { get => GetValue(ToolTipSurfaceBrushProperty); set => SetValue(ToolTipSurfaceBrushProperty, value); }
    public IBrush? ToolTipBorderBrush { get => GetValue(ToolTipBorderBrushProperty); set => SetValue(ToolTipBorderBrushProperty, value); }

    public int MinimumCount { get; private set; } = 1;
    public double LabelThresholdRatio { get; private set; } = 0.65;
    public int VisibleSampleCount { get; private set; }
    public int VisibleCellCount { get; private set; }
    public int PeakCount { get; private set; }
    public HeatmapHoverCell? HoveredCell => hoveredCell;

    public AnalysisHeatmapSurface()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void Show(AnalysisHotspot? value, int minimumCount = 1, double labelThresholdRatio = 0.65)
    {
        hotspot = value;
        MinimumCount = Math.Max(1, minimumCount);
        LabelThresholdRatio = Math.Clamp(labelThresholdRatio, 0, 1);
        var visible = value?.Counts.Where(count => count >= MinimumCount).ToArray() ?? [];
        VisibleSampleCount = visible.Sum();
        VisibleCellCount = visible.Length;
        PeakCount = visible.DefaultIfEmpty().Max();
        hoveredCell = null;
        InvalidateVisual();
    }

    public void ClearHover()
    {
        if (hoveredCell is null) return;
        hoveredCell = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 1 || bounds.Height <= 1) return;

        var plot = Plot(bounds);
        context.DrawRectangle(null, new Pen(GridBrush, 1), plot);
        DrawGuideLines(context, plot);

        if (hotspot is not { SampleCount: > 0 } data || data.Counts.Count == 0)
        {
            DrawText(context, "No coordinate samples in range", new Point(plot.X + 12, plot.Y + 12), 13, FontWeight.Medium);
            return;
        }

        if (PeakCount == 0)
        {
            DrawText(context, $"No grid cells meet the minimum count of {MinimumCount:N0}", new Point(plot.X + 12, plot.Y + 12), 13, FontWeight.Medium);
            DrawAxes(context, plot, data);
            DrawColorBar(context, plot, 0);
            return;
        }

        var cellWidth = plot.Width / data.Columns;
        var cellHeight = plot.Height / data.Rows;
        var minimumRadius = Math.Max(3, Math.Min(cellWidth, cellHeight) * 0.20);
        var radiusScale = Math.Max(4, Math.Min(cellWidth, cellHeight) * 1.45);
        for (var row = 0; row < data.Rows; row++)
        {
            for (var column = 0; column < data.Columns; column++)
            {
                var count = data.Counts[(row * data.Columns) + column];
                if (count < MinimumCount) continue;
                var peakRatio = count / (double)PeakCount;
                var colorRatio = PeakCount == MinimumCount
                    ? 1
                    : (count - MinimumCount) / (double)(PeakCount - MinimumCount);
                var radius = minimumRadius + (radiusScale * Math.Sqrt(peakRatio));
                var center = new Point(
                    plot.X + ((column + 0.5) * cellWidth),
                    plot.Y + ((row + 0.5) * cellHeight));
                var color = ColorAt(colorRatio);
                var fill = new SolidColorBrush(color, 0.28 + (0.58 * colorRatio));
                var stroke = new SolidColorBrush(color, 0.78 + (0.22 * colorRatio));
                context.DrawEllipse(fill, new Pen(stroke, 1.15), center, radius, radius);

                if (peakRatio >= LabelThresholdRatio)
                    DrawCountLabel(context, count, center, colorRatio);
            }
        }

        DrawAxes(context, plot, data);
        DrawColorBar(context, plot, PeakCount);
        if (hoveredCell is { } hover)
            DrawHoverCard(context, plot, hover);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var data = hotspot;
        if (data is null || data.Counts.Count == 0)
        {
            ClearHover();
            return;
        }

        var plot = Plot(new Rect(Bounds.Size));
        var point = e.GetPosition(this);
        if (!plot.Contains(point))
        {
            ClearHover();
            return;
        }

        var column = Math.Clamp((int)((point.X - plot.X) / plot.Width * data.Columns), 0, data.Columns - 1);
        var row = Math.Clamp((int)((point.Y - plot.Y) / plot.Height * data.Rows), 0, data.Rows - 1);
        var count = data.Counts[(row * data.Columns) + column];
        if (count < MinimumCount || PeakCount == 0)
        {
            ClearHover();
            return;
        }

        var next = new HeatmapHoverCell(
            column,
            row,
            count,
            count / (double)PeakCount,
            data.Transform.MinimumX + ((data.Transform.MaximumX - data.Transform.MinimumX) * column / data.Columns),
            data.Transform.MinimumX + ((data.Transform.MaximumX - data.Transform.MinimumX) * (column + 1) / data.Columns),
            data.Transform.MinimumY + ((data.Transform.MaximumY - data.Transform.MinimumY) * row / data.Rows),
            data.Transform.MinimumY + ((data.Transform.MaximumY - data.Transform.MinimumY) * (row + 1) / data.Rows));
        if (Equals(hoveredCell, next)) return;
        hoveredCell = next;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHover();
    }

    public static Color ColorAt(double normalized)
    {
        var value = Math.Clamp(normalized, 0, 1);
        for (var index = 1; index < Palette.Length; index++)
        {
            if (value > Palette[index].Stop) continue;
            var previous = Palette[index - 1];
            var next = Palette[index];
            var fraction = (value - previous.Stop) / (next.Stop - previous.Stop);
            return Color.FromRgb(
                Lerp(previous.Color.R, next.Color.R, fraction),
                Lerp(previous.Color.G, next.Color.G, fraction),
                Lerp(previous.Color.B, next.Color.B, fraction));
        }
        return Palette[^1].Color;
    }

    private static byte Lerp(byte start, byte end, double fraction) =>
        (byte)Math.Round(start + ((end - start) * fraction));

    private static Rect Plot(Rect bounds)
    {
        const double left = 58;
        const double top = 24;
        const double right = 112;
        const double bottom = 46;
        return new Rect(left, top, Math.Max(1, bounds.Width - left - right), Math.Max(1, bounds.Height - top - bottom));
    }

    private void DrawGuideLines(DrawingContext context, Rect plot)
    {
        var pen = new Pen(GridBrush, 0.8);
        for (var division = 1; division < 4; division++)
        {
            var x = plot.X + (plot.Width * division / 4);
            var y = plot.Y + (plot.Height * division / 4);
            context.DrawLine(pen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            context.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));
        }
    }

    private void DrawAxes(DrawingContext context, Rect plot, AnalysisHotspot data)
    {
        DrawText(context, "0", new Point(plot.X - 2, plot.Bottom + 10), 11.5, FontWeight.Medium);
        DrawText(context, data.Transform.MaximumX.ToString("0", CultureInfo.InvariantCulture), new Point(plot.Right - 42, plot.Bottom + 10), 11.5, FontWeight.Medium);
        DrawText(context, "X", new Point(plot.Center.X, plot.Bottom + 23), 12.5, FontWeight.SemiBold);
        DrawText(context, "0", new Point(28, plot.Y - 7), 11.5, FontWeight.Medium);
        DrawText(context, data.Transform.MaximumY.ToString("0", CultureInfo.InvariantCulture), new Point(8, plot.Bottom - 8), 11.5, FontWeight.Medium);
        DrawText(context, "Y", new Point(17, plot.Center.Y - 7), 12.5, FontWeight.SemiBold);
    }

    private void DrawColorBar(DrawingContext context, Rect plot, int peak)
    {
        var barHeight = Math.Max(60, plot.Height - 54);
        var bar = new Rect(plot.Right + 30, plot.Y + 27, 15, barHeight);
        IBrush scaleBrush = peak <= MinimumCount ? new SolidColorBrush(ColorAt(1)) : ColorBarBrush;
        context.DrawRectangle(scaleBrush, new Pen(GridBrush, 1), bar, 2, 2);
        DrawText(context, "PEAK", new Point(bar.X - 3, bar.Y - 22), 10.5, FontWeight.SemiBold);
        DrawText(context, peak.ToString("N0", CultureInfo.InvariantCulture), new Point(bar.Right + 8, bar.Y - 5), 11.5, FontWeight.SemiBold);
        if (peak - MinimumCount >= 2)
            DrawText(context, Math.Round((peak + MinimumCount) * 0.5).ToString("N0", CultureInfo.InvariantCulture), new Point(bar.Right + 8, bar.Center.Y - 7), 11, FontWeight.Medium);
        if (peak > MinimumCount)
            DrawText(context, MinimumCount.ToString("N0", CultureInfo.InvariantCulture), new Point(bar.Right + 8, bar.Bottom - 8), 11, FontWeight.Medium);
    }

    private void DrawCountLabel(DrawingContext context, int count, Point center, double colorRatio)
    {
        var text = new FormattedText(
            count.ToString("N0", CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Bold),
            11,
            colorRatio is >= 0.56 and <= 0.88 ? DarkLabelBrush : LightLabelBrush);
        context.DrawText(text, new Point(center.X - (text.Width / 2), center.Y - (text.Height / 2)));
    }

    private void DrawHoverCard(DrawingContext context, Rect plot, HeatmapHoverCell hover)
    {
        var line1 = $"{hover.Count:N0} samples · {hover.PeakRatio:P1} of peak";
        var line2 = $"X {hover.MinimumX:0}-{hover.MaximumX:0} · Y {hover.MinimumY:0}-{hover.MaximumY:0}";
        var first = Text(line1, 12.5, FontWeight.SemiBold, TextBrush);
        var second = Text(line2, 11.5, FontWeight.Medium, TextBrush);
        var width = Math.Max(first.Width, second.Width) + 22;
        var height = first.Height + second.Height + 15;
        var cellCenterX = plot.X + ((hover.Column + 0.5) * plot.Width / Math.Max(1, hotspot!.Columns));
        var cellCenterY = plot.Y + ((hover.Row + 0.5) * plot.Height / Math.Max(1, hotspot.Rows));
        var x = cellCenterX + 18;
        if (x + width > plot.Right) x = cellCenterX - width - 18;
        var y = Math.Clamp(cellCenterY - (height / 2), plot.Y + 4, plot.Bottom - height - 4);
        var rect = new Rect(x, y, width, height);
        context.DrawRectangle(ToolTipSurfaceBrush, new Pen(ToolTipBorderBrush, 1), rect, 4, 4);
        context.DrawText(first, new Point(rect.X + 11, rect.Y + 7));
        context.DrawText(second, new Point(rect.X + 11, rect.Y + 8 + first.Height));
    }

    private void DrawText(DrawingContext context, string value, Point origin, double size, FontWeight weight) =>
        context.DrawText(Text(value, size, weight, TextBrush), origin);

    private static FormattedText Text(string value, double size, FontWeight weight, IBrush? brush) =>
        new(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, weight),
            size,
            brush);

    private static LinearGradientBrush CreateColorBarBrush()
    {
        var result = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        };
        foreach (var (stop, color) in Palette)
            result.GradientStops.Add(new GradientStop(color, stop));
        return result;
    }
}
