using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Nvt.Replay.Analysis;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class AnalysisHeatmapSurface : Control
{
    private static readonly IBrush LightHeatmapBrush = new SolidColorBrush(Color.Parse("#416700"));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<AnalysisHeatmapSurface, IBrush?>(nameof(TextBrush));

    private AnalysisHotspot? hotspot;

    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public void Show(AnalysisHotspot? value)
    {
        hotspot = value;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 1 || bounds.Height <= 1) return;

        const double left = 54;
        const double top = 20;
        const double right = 28;
        const double bottom = 42;
        var plot = new Rect(left, top, Math.Max(1, bounds.Width - left - right), Math.Max(1, bounds.Height - top - bottom));
        context.DrawRectangle(null, new Pen(GridBrush, 1), plot);
        DrawGuideLines(context, plot);

        if (hotspot is not { SampleCount: > 0 } data || data.Counts.Count == 0)
        {
            DrawText(context, "No coordinate samples in range", new Point(plot.X + 12, plot.Y + 12), 12, FontWeight.Medium);
            return;
        }

        var peak = Math.Max(1, data.Counts.Max());
        var heatmapBrush = ActualThemeVariant == ThemeVariant.Light ? LightHeatmapBrush : AccentBrush;
        var cellWidth = plot.Width / data.Columns;
        var cellHeight = plot.Height / data.Rows;
        var baseRadius = Math.Max(2.5, Math.Min(cellWidth, cellHeight) * 0.22);
        var radiusScale = Math.Max(4, Math.Min(cellWidth, cellHeight) * 1.55);
        for (var row = 0; row < data.Rows; row++)
        {
            for (var column = 0; column < data.Columns; column++)
            {
                var count = data.Counts[(row * data.Columns) + column];
                if (count == 0) continue;
                var normalized = Math.Log(1 + count) / Math.Log(1 + peak);
                var radius = baseRadius + (radiusScale * Math.Sqrt(count / (double)peak));
                var center = new Point(
                    plot.X + ((column + 0.5) * cellWidth),
                    plot.Y + ((row + 0.5) * cellHeight));
                using (context.PushOpacity(0.14 + (0.48 * normalized)))
                    context.DrawEllipse(heatmapBrush, null, center, radius, radius);
                using (context.PushOpacity(0.45 + (0.45 * normalized)))
                    context.DrawEllipse(null, new Pen(heatmapBrush, 1.1), center, radius, radius);
            }
        }

        DrawText(context, "0", new Point(plot.X - 2, plot.Bottom + 9), 11, FontWeight.Medium);
        DrawText(context, data.Transform.MaximumX.ToString("0", CultureInfo.InvariantCulture), new Point(plot.Right - 42, plot.Bottom + 9), 11, FontWeight.Medium);
        DrawText(context, "X", new Point(plot.Center.X, plot.Bottom + 20), 12, FontWeight.SemiBold);
        DrawText(context, "0", new Point(24, plot.Y - 6), 11, FontWeight.Medium);
        DrawText(context, data.Transform.MaximumY.ToString("0", CultureInfo.InvariantCulture), new Point(8, plot.Bottom - 8), 11, FontWeight.Medium);
        DrawText(context, "Y", new Point(14, plot.Center.Y - 7), 12, FontWeight.SemiBold);
    }

    private void DrawGuideLines(DrawingContext context, Rect plot)
    {
        var pen = new Pen(GridBrush, 0.75);
        for (var division = 1; division < 4; division++)
        {
            var x = plot.X + (plot.Width * division / 4);
            var y = plot.Y + (plot.Height * division / 4);
            context.DrawLine(pen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            context.DrawLine(pen, new Point(plot.X, y), new Point(plot.Right, y));
        }
    }

    private void DrawText(DrawingContext context, string value, Point origin, double size, FontWeight weight)
    {
        var text = new FormattedText(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, weight),
            size,
            TextBrush);
        context.DrawText(text, origin);
    }
}
