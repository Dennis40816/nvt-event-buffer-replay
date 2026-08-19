using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Nvt.Replay.Rendering;
using SkiaSharp;

namespace Nvt.Replay.Avalonia.Controls;

internal readonly record struct ReplayTrailDrawBatch(
    byte ContactId,
    SKPoint[] LinePoints,
    SKPoint[] ReportPoints);

internal sealed class ReplayTrailBatchCache
{
    private const int MaximumEntries = 1024;
    private readonly Dictionary<CacheKey, BatchCoordinates> entries = new();
    private readonly Queue<CacheKey> insertionOrder = new();

    internal int Count => entries.Count;
    internal long CoordinateBuildPointCount { get; private set; }

    public IReadOnlyList<ReplayTrailDrawBatch> Build(ReplayScene scene)
    {
        var result = new List<ReplayTrailDrawBatch>();
        foreach (var trail in scene.ContactTrails)
        {
            foreach (var chunk in ReplayTrailChunker.Enumerate(trail))
            {
                var coordinates = chunk.IsCacheable
                    ? GetOrCreate(chunk, scene.SwapAxes)
                    : CreateCoordinates(chunk, scene.SwapAxes);
                result.Add(new ReplayTrailDrawBatch(
                    trail.Id,
                    coordinates.LinePoints,
                    coordinates.ReportPoints));
            }
        }
        return result;
    }

    public void Clear()
    {
        entries.Clear();
        insertionOrder.Clear();
        CoordinateBuildPointCount = 0;
    }

    private BatchCoordinates GetOrCreate(ReplayTrailChunk chunk, bool swapAxes)
    {
        var key = new CacheKey(
            chunk.Source,
            chunk.Offset,
            chunk.Count,
            chunk.LeadingOffset,
            swapAxes);
        if (entries.TryGetValue(key, out var existing)) return existing;

        var created = CreateCoordinates(chunk, swapAxes);
        entries[key] = created;
        insertionOrder.Enqueue(key);
        while (entries.Count > MaximumEntries && insertionOrder.TryDequeue(out var oldest))
            entries.Remove(oldest);
        return created;
    }

    private BatchCoordinates CreateCoordinates(ReplayTrailChunk chunk, bool swapAxes)
    {
        CoordinateBuildPointCount += chunk.Count;
        var lineOffset = chunk.LeadingOffset is null ? 0 : 1;
        var linePoints = new SKPoint[chunk.Count + lineOffset];
        if (chunk.LeadingOffset is not null)
            linePoints[0] = Convert(chunk.LeadingPoint, swapAxes);

        var reportPoints = new SKPoint[chunk.Count];
        for (var index = 0; index < chunk.Count; index++)
        {
            var point = Convert(chunk[index], swapAxes);
            linePoints[index + lineOffset] = point;
            reportPoints[index] = point;
        }
        return new BatchCoordinates(linePoints, reportPoints);
    }

    private static SKPoint Convert(ReplayTrailPoint point, bool swapAxes) => swapAxes
        ? new SKPoint(point.Y, point.X)
        : new SKPoint(point.X, point.Y);

    private sealed record BatchCoordinates(SKPoint[] LinePoints, SKPoint[] ReportPoints);

    private readonly struct CacheKey : IEquatable<CacheKey>
    {
        private readonly object source;
        private readonly int offset;
        private readonly int count;
        private readonly int leadingOffset;
        private readonly bool swapAxes;

        public CacheKey(object source, int offset, int count, int? leadingOffset, bool swapAxes)
        {
            this.source = source;
            this.offset = offset;
            this.count = count;
            this.leadingOffset = leadingOffset ?? -1;
            this.swapAxes = swapAxes;
        }

        public bool Equals(CacheKey other) =>
            ReferenceEquals(source, other.source) &&
            offset == other.offset &&
            count == other.count &&
            leadingOffset == other.leadingOffset &&
            swapAxes == other.swapAxes;

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(
            RuntimeHelpers.GetHashCode(source),
            offset,
            count,
            leadingOffset,
            swapAxes);
    }
}

internal sealed class ReplayTrailDrawOperation : ICustomDrawOperation
{
    private readonly ReplayExtent extent;
    private readonly IReadOnlyList<ReplayTrailDrawBatch> batches;
    private readonly IReadOnlyList<Color> contactColors;
    private readonly bool reverseX;
    private readonly bool reverseY;
    private readonly bool showLines;
    private readonly bool showPoints;
    private readonly double opacity;

    public ReplayTrailDrawOperation(
        Rect viewport,
        ReplayExtent extent,
        IReadOnlyList<ReplayTrailDrawBatch> batches,
        IReadOnlyList<Color> contactColors,
        bool reverseX,
        bool reverseY,
        bool showLines,
        bool showPoints,
        double opacity)
    {
        Bounds = viewport;
        this.extent = extent;
        this.batches = batches;
        this.contactColors = contactColors;
        this.reverseX = reverseX;
        this.reverseY = reverseY;
        this.showLines = showLines;
        this.showPoints = showPoints;
        this.opacity = Math.Clamp(opacity, 0, 1);
    }

    public Rect Bounds { get; }

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature is null || batches.Count == 0 || (!showLines && !showPoints)) return;

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        var scaleX = Bounds.Width / Math.Max(1, extent.MaximumX);
        var scaleY = Bounds.Height / Math.Max(1, extent.MaximumY);
        var strokeScale = Math.Max(0.0001, Math.Min(Math.Abs(scaleX), Math.Abs(scaleY)));
        var saved = canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect(
                (float)Bounds.Left,
                (float)Bounds.Top,
                (float)Bounds.Right,
                (float)Bounds.Bottom));
            canvas.Translate(
                (float)(reverseX ? Bounds.Right : Bounds.Left),
                (float)(reverseY ? Bounds.Bottom : Bounds.Top));
            canvas.Scale(
                (float)(reverseX ? -scaleX : scaleX),
                (float)(reverseY ? -scaleY : scaleY));

            using var linePaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(1.6 / strokeScale),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round,
            };
            using var pointPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (float)(4.7 / strokeScale),
                StrokeCap = SKStrokeCap.Round,
            };

            foreach (var batch in batches)
            {
                var color = contactColors[(Math.Max(1, (int)batch.ContactId) - 1) % contactColors.Count];
                if (showLines && batch.LinePoints.Length > 1)
                {
                    linePaint.Color = ToSkColor(color, 150 / 255d * opacity);
                    canvas.DrawPoints(SKPointMode.Polygon, batch.LinePoints, linePaint);
                }
                if (showPoints && batch.ReportPoints.Length > 0)
                {
                    pointPaint.Color = ToSkColor(color, opacity);
                    canvas.DrawPoints(SKPointMode.Points, batch.ReportPoints, pointPaint);
                }
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

    private static SKColor ToSkColor(Color color, double opacity) => new(
        color.R,
        color.G,
        color.B,
        (byte)Math.Clamp(Math.Round(color.A * opacity), 0, 255));
}
