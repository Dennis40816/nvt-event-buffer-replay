using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using SkiaSharp;

namespace Nvt.Replay.Avalonia.Controls;

public sealed record ReportedPointHover(
    int LogicalIndex,
    byte ContactId,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y);

public sealed class AnalysisPointPlotSurface : Control
{
    private const int SpatialColumns = 64;
    private const int SpatialRows = 36;
    private static readonly Typeface AxisTypeface = new("Cascadia Mono", FontStyle.Normal, FontWeight.Medium);

    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty =
        AvaloniaProperty.Register<AnalysisPointPlotSurface, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<AnalysisPointPlotSurface, IBrush?>(nameof(GridBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<AnalysisPointPlotSurface, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> ToolTipSurfaceBrushProperty =
        AvaloniaProperty.Register<AnalysisPointPlotSurface, IBrush?>(nameof(ToolTipSurfaceBrush));
    public static readonly StyledProperty<IBrush?> ToolTipBorderBrushProperty =
        AvaloniaProperty.Register<AnalysisPointPlotSurface, IBrush?>(nameof(ToolTipBorderBrush));

    private IReadOnlyList<AnalysisEvent> events = [];
    private ReportedPointSample[] points = [];
    private IReadOnlyList<ReportedPointBatch> batches = [];
    private List<int>?[] spatialCells = new List<int>?[SpatialColumns * SpatialRows];
    private double maximumX = 1;
    private double maximumY = 1;
    private ReportedPointHover? hoveredPoint;

    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? ToolTipSurfaceBrush { get => GetValue(ToolTipSurfaceBrushProperty); set => SetValue(ToolTipSurfaceBrushProperty, value); }
    public IBrush? ToolTipBorderBrush { get => GetValue(ToolTipBorderBrushProperty); set => SetValue(ToolTipBorderBrushProperty, value); }

    public int PointCount => points.Length;
    public int ContactIdCount => batches.Count;
    public int OccupiedSpatialCellCount { get; private set; }
    public ReportedPointHover? HoveredPoint => hoveredPoint;
    public ReplayRenderTheme? RenderThemeOverride { get; set; }

    public AnalysisPointPlotSurface()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void Show(CaptureAnalysisReport? report, double panelMaximumX, double panelMaximumY) =>
        ShowEvents(report?.Events, panelMaximumX, panelMaximumY);

    public void ShowEvents(IReadOnlyList<AnalysisEvent>? value, double panelMaximumX, double panelMaximumY)
    {
        value ??= [];
        var nextMaximumX = Math.Max(1, panelMaximumX);
        var nextMaximumY = Math.Max(1, panelMaximumY);
        if (ReferenceEquals(events, value) &&
            Math.Abs(maximumX - nextMaximumX) < 0.001 &&
            Math.Abs(maximumY - nextMaximumY) < 0.001)
        {
            ClearHover();
            return;
        }

        events = value;
        maximumX = nextMaximumX;
        maximumY = nextMaximumY;
        var collected = new List<ReportedPointSample>();
        foreach (var analysisEvent in value)
        {
            foreach (var contact in analysisEvent.ReportedContacts)
            {
                if (!AnalysisContactRules.IsCoordinateSample(contact)) continue;
                collected.Add(new ReportedPointSample(
                    analysisEvent.LogicalIndex,
                    contact.Id,
                    contact.Type,
                    contact.Status,
                    contact.X,
                    contact.Y));
            }
        }
        points = collected.ToArray();

        batches = points
            .GroupBy(point => point.ContactId)
            .OrderBy(group => group.Key)
            .Select(group => new ReportedPointBatch(
                group.Key,
                group.Select(point => new SKPoint(point.X, point.Y)).ToArray()))
            .ToArray();

        spatialCells = new List<int>?[SpatialColumns * SpatialRows];
        OccupiedSpatialCellCount = 0;
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            var column = SpatialColumn(point.X);
            var row = SpatialRow(point.Y);
            var cellIndex = (row * SpatialColumns) + column;
            var cell = spatialCells[cellIndex];
            if (cell is null)
            {
                spatialCells[cellIndex] = cell = [];
                OccupiedSpatialCellCount++;
            }
            cell.Add(index);
        }

        hoveredPoint = null;
        InvalidateVisual();
    }

    public void ClearHover()
    {
        if (hoveredPoint is null) return;
        hoveredPoint = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 1 || bounds.Height <= 1) return;
        context.DrawRectangle(SurfaceBrush, null, bounds);
        var plot = Plot(bounds);
        context.DrawRectangle(null, new Pen(GridBrush, 1), plot);
        DrawGuideLines(context, plot);
        if (points.Length == 0)
        {
            DrawText(context, "No reported coordinate samples in range", new Point(plot.X + 12, plot.Y + 12), 13, FontWeight.Medium);
            DrawAxes(context, plot);
            return;
        }

        var palette = ReplayVisualStyle.For(RenderThemeOverride ?? (ActualThemeVariant == ThemeVariant.Light
            ? ReplayRenderTheme.Light
            : ReplayRenderTheme.Dark));
        context.Custom(new ReportedPointDrawOperation(plot, maximumX, maximumY, batches, palette));
        DrawAxes(context, plot);
        if (hoveredPoint is { } hover) DrawHoverCard(context, plot, hover);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var plot = Plot(new Rect(Bounds.Size));
        var pointer = e.GetPosition(this);
        if (!plot.Contains(pointer) || points.Length == 0)
        {
            ClearHover();
            return;
        }

        var sourceX = Math.Clamp((pointer.X - plot.X) / plot.Width * maximumX, 0, maximumX);
        var sourceY = Math.Clamp((pointer.Y - plot.Y) / plot.Height * maximumY, 0, maximumY);
        var column = SpatialColumn(sourceX);
        var row = SpatialRow(sourceY);
        var bestIndex = -1;
        var bestDistanceSquared = 14d * 14d;
        for (var rowOffset = -1; rowOffset <= 1; rowOffset++)
        {
            var candidateRow = row + rowOffset;
            if (candidateRow is < 0 or >= SpatialRows) continue;
            for (var columnOffset = -1; columnOffset <= 1; columnOffset++)
            {
                var candidateColumn = column + columnOffset;
                if (candidateColumn is < 0 or >= SpatialColumns) continue;
                var cell = spatialCells[(candidateRow * SpatialColumns) + candidateColumn];
                if (cell is null) continue;
                foreach (var pointIndex in cell)
                {
                    var candidate = points[pointIndex];
                    var screenX = plot.X + (candidate.X / maximumX * plot.Width);
                    var screenY = plot.Y + (candidate.Y / maximumY * plot.Height);
                    var dx = pointer.X - screenX;
                    var dy = pointer.Y - screenY;
                    var distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared > bestDistanceSquared) continue;
                    if (distanceSquared < bestDistanceSquared ||
                        bestIndex < 0 || candidate.LogicalIndex >= points[bestIndex].LogicalIndex)
                    {
                        bestDistanceSquared = distanceSquared;
                        bestIndex = pointIndex;
                    }
                }
            }
        }

        var next = bestIndex < 0
            ? null
            : points[bestIndex].ToHover();
        if (Equals(hoveredPoint, next)) return;
        hoveredPoint = next;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ClearHover();
    }

    private int SpatialColumn(double x) =>
        Math.Clamp((int)(x / maximumX * SpatialColumns), 0, SpatialColumns - 1);

    private int SpatialRow(double y) =>
        Math.Clamp((int)(y / maximumY * SpatialRows), 0, SpatialRows - 1);

    private static Rect Plot(Rect bounds)
    {
        const double left = 58;
        const double top = 24;
        const double right = 28;
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

    private void DrawAxes(DrawingContext context, Rect plot)
    {
        DrawText(context, "0", new Point(plot.X - 4, plot.Bottom + 7), 11, FontWeight.Medium);
        DrawText(context, maximumX.ToString("0", CultureInfo.InvariantCulture), new Point(plot.Right - 30, plot.Bottom + 7), 11, FontWeight.Medium);
        DrawText(context, "0", new Point(plot.X - 28, plot.Y - 6), 11, FontWeight.Medium);
        DrawText(context, maximumY.ToString("0", CultureInfo.InvariantCulture), new Point(plot.X - 42, plot.Bottom - 7), 11, FontWeight.Medium);
        DrawText(context, "X", new Point(plot.Center.X, plot.Bottom + 24), 11, FontWeight.SemiBold);
        DrawText(context, "Y", new Point(plot.X - 42, plot.Center.Y), 11, FontWeight.SemiBold);
    }

    private void DrawHoverCard(DrawingContext context, Rect plot, ReportedPointHover hover)
    {
        var line1 = $"Frame {hover.LogicalIndex + 1:N0}  ID {hover.ContactId}";
        var line2 = $"{hover.Type} / {hover.Status}";
        var line3 = $"X {hover.X:N0}  Y {hover.Y:N0}";
        var text1 = Formatted(line1, 12, FontWeight.SemiBold);
        var text2 = Formatted(line2, 11.5, FontWeight.Medium);
        var text3 = Formatted(line3, 11.5, FontWeight.Medium);
        var width = Math.Max(text1.Width, Math.Max(text2.Width, text3.Width)) + 20;
        var height = text1.Height + text2.Height + text3.Height + 18;
        var pointX = plot.X + (hover.X / maximumX * plot.Width);
        var pointY = plot.Y + (hover.Y / maximumY * plot.Height);
        var cardX = Math.Clamp(pointX + 12, plot.X + 4, Math.Max(plot.X + 4, plot.Right - width - 4));
        var cardY = Math.Clamp(pointY - height - 12, plot.Y + 4, Math.Max(plot.Y + 4, plot.Bottom - height - 4));
        var rect = new Rect(cardX, cardY, width, height);
        context.DrawRectangle(ToolTipSurfaceBrush, new Pen(ToolTipBorderBrush, 1), rect, 3, 3);
        context.DrawText(text1, new Point(rect.X + 10, rect.Y + 7));
        context.DrawText(text2, new Point(rect.X + 10, rect.Y + 7 + text1.Height + 2));
        context.DrawText(text3, new Point(rect.X + 10, rect.Y + 9 + text1.Height + text2.Height + 2));
    }

    private void DrawText(DrawingContext context, string value, Point origin, double size, FontWeight weight) =>
        context.DrawText(Formatted(value, size, weight), origin);

    private FormattedText Formatted(string value, double size, FontWeight weight) =>
        new(
            value,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(AxisTypeface.FontFamily, FontStyle.Normal, weight),
            size,
            TextBrush);

    private readonly record struct ReportedPointSample(
        int LogicalIndex,
        byte ContactId,
        TouchType Type,
        TouchStatus Status,
        ushort X,
        ushort Y)
    {
        public ReportedPointHover ToHover() => new(LogicalIndex, ContactId, Type, Status, X, Y);
    }
}

internal sealed record ReportedPointBatch(byte ContactId, SKPoint[] Points);

internal sealed class ReportedPointDrawOperation : ICustomDrawOperation
{
    private readonly double maximumX;
    private readonly double maximumY;
    private readonly IReadOnlyList<ReportedPointBatch> batches;
    private readonly ReplayVisualPalette palette;

    public ReportedPointDrawOperation(
        Rect bounds,
        double maximumX,
        double maximumY,
        IReadOnlyList<ReportedPointBatch> batches,
        ReplayVisualPalette palette)
    {
        Bounds = bounds;
        this.maximumX = Math.Max(1, maximumX);
        this.maximumY = Math.Max(1, maximumY);
        this.batches = batches;
        this.palette = palette;
    }

    public Rect Bounds { get; }

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null || batches.Count == 0) return;

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        var scaleX = Bounds.Width / maximumX;
        var scaleY = Bounds.Height / maximumY;
        var strokeScale = Math.Max(0.0001, Math.Min(Math.Abs(scaleX), Math.Abs(scaleY)));
        var saved = canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect((float)Bounds.Left, (float)Bounds.Top, (float)Bounds.Right, (float)Bounds.Bottom));
            canvas.Translate((float)Bounds.Left, (float)Bounds.Top);
            canvas.Scale((float)scaleX, (float)scaleY);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(3.2 / strokeScale),
                StrokeCap = SKStrokeCap.Round,
            };
            foreach (var batch in batches)
            {
                var color = ReplayVisualStyle.ContactColor(palette, batch.ContactId);
                paint.Color = new SKColor(color.Red, color.Green, color.Blue, 178);
                canvas.DrawPoints(SKPointMode.Points, batch.Points, paint);
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    public bool HitTest(Point point) => false;
    public bool Equals(ICustomDrawOperation? other) => false;
    public void Dispose() { }
}
