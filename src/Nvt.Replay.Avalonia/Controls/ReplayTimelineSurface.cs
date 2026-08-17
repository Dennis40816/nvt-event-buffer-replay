using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class ReplayTimelineSurface : Control, ICustomHitTest
{
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> HoverTrackBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(HoverTrackBrush));
    public static readonly StyledProperty<IBrush?> PhysicalBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(PhysicalBrush));
    public static readonly StyledProperty<IBrush?> LogicalBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(LogicalBrush));
    public static readonly StyledProperty<IBrush?> EvidenceBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(EvidenceBrush));
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(PlayheadBrush));
    public static readonly StyledProperty<IBrush?> LoopBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(LoopBrush));
    public static readonly StyledProperty<IBrush?> LoopSurfaceBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(LoopSurfaceBrush));

    private const double TrackInset = 5;
    private int maximum = 1;
    private int value;
    private double physicalFraction;
    private double evidenceFraction;
    private int? loopStart;
    private int? loopEnd;
    private bool loopEnabled;
    private int? hoverFrame;
    private DragTarget dragTarget;

    public event EventHandler<ReplayTimelineSeekEventArgs>? SeekRequested;
    public event EventHandler<ReplayTimelineLoopEventArgs>? LoopRangeChanged;
    public event EventHandler<ReplayTimelineContextEventArgs>? ContextFrameRequested;

    public ReplayTimelineSurface()
    {
        ContextRequested += OnContextRequested;
    }

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? HoverTrackBrush { get => GetValue(HoverTrackBrushProperty); set => SetValue(HoverTrackBrushProperty, value); }
    public IBrush? PhysicalBrush { get => GetValue(PhysicalBrushProperty); set => SetValue(PhysicalBrushProperty, value); }
    public IBrush? LogicalBrush { get => GetValue(LogicalBrushProperty); set => SetValue(LogicalBrushProperty, value); }
    public IBrush? EvidenceBrush { get => GetValue(EvidenceBrushProperty); set => SetValue(EvidenceBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public IBrush? LoopBrush { get => GetValue(LoopBrushProperty); set => SetValue(LoopBrushProperty, value); }
    public IBrush? LoopSurfaceBrush { get => GetValue(LoopSurfaceBrushProperty); set => SetValue(LoopSurfaceBrushProperty, value); }

    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public void SetMaximum(int value)
    {
        maximum = Math.Max(1, value);
        this.value = Math.Clamp(this.value, 0, maximum);
        InvalidateVisual();
    }

    public void SetPosition(int logicalIndex)
    {
        value = Math.Clamp(logicalIndex, 0, maximum);
        InvalidateVisual();
    }

    public void SetSupportingProgress(double physical, double evidence)
    {
        physicalFraction = Math.Clamp(physical, 0, 1);
        evidenceFraction = Math.Clamp(evidence, 0, 1);
        InvalidateVisual();
    }

    public void SetLoopRange(int? start, int? end, bool enabled)
    {
        loopStart = start is null ? null : Math.Clamp(start.Value, 0, maximum);
        loopEnd = end is null ? null : Math.Clamp(end.Value, 0, maximum);
        loopEnabled = enabled && loopStart is not null && loopEnd is not null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= TrackInset * 2 || Bounds.Height <= 1) return;

        var physicalY = Bounds.Height * 0.125;
        var logicalY = Bounds.Height * 0.5;
        var evidenceY = Bounds.Height * 0.875;
        var trackThickness = IsPointerOver || dragTarget != DragTarget.None ? 2.5 : 1;
        var trackBrush = IsPointerOver ? HoverTrackBrush : TrackBrush;
        var left = TrackInset;
        var right = Bounds.Width - TrackInset;

        DrawTrack(context, physicalY, physicalFraction, PhysicalBrush, trackBrush, trackThickness);
        DrawTrack(context, logicalY, maximum == 0 ? 0 : value / (double)maximum, LogicalBrush, trackBrush, trackThickness);
        DrawTrack(context, evidenceY, evidenceFraction, EvidenceBrush, trackBrush, trackThickness);

        if (loopStart is { } start && loopEnd is { } end)
        {
            var startX = PositionFor(start);
            var endX = PositionFor(end);
            if (endX < startX) (startX, endX) = (endX, startX);
            if (loopEnabled)
            {
                var selection = new Rect(startX, logicalY - 8, Math.Max(1, endX - startX), 16);
                context.DrawRectangle(LoopSurfaceBrush, null, selection, 2, 2);
            }
            var loopPen = new Pen(loopEnabled ? LoopBrush : HoverTrackBrush, loopEnabled ? 2.5 : 1.5);
            context.DrawLine(loopPen, new Point(startX, logicalY - 10), new Point(startX, logicalY + 10));
            context.DrawLine(loopPen, new Point(startX, logicalY - 10), new Point(startX + 6, logicalY - 10));
            context.DrawLine(loopPen, new Point(startX, logicalY + 10), new Point(startX + 6, logicalY + 10));
            context.DrawLine(loopPen, new Point(endX, logicalY - 10), new Point(endX, logicalY + 10));
            context.DrawLine(loopPen, new Point(endX - 6, logicalY - 10), new Point(endX, logicalY - 10));
            context.DrawLine(loopPen, new Point(endX - 6, logicalY + 10), new Point(endX, logicalY + 10));
        }

        var playheadX = Math.Clamp(PositionFor(value), left, right);
        var playheadPen = new Pen(PlayheadBrush, IsPointerOver ? 2 : 1.5);
        context.DrawLine(playheadPen, new Point(playheadX, 2), new Point(playheadX, Bounds.Height - 2));
        context.DrawLine(playheadPen, new Point(playheadX - 3, 2), new Point(playheadX + 3, 2));

        if (hoverFrame is { } frame && IsPointerOver)
            DrawHoverLabel(context, frame, PositionFor(frame));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (dragTarget == DragTarget.None) hoverFrame = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
            return;
        }

        base.OnPointerPressed(e);
        var position = e.GetPosition(this);
        var frame = FrameFor(position.X);
        if (loopEnabled && loopStart is { } start && Math.Abs(PositionFor(start) - position.X) <= 9)
        {
            dragTarget = DragTarget.LoopStart;
        }
        else if (loopEnabled && loopEnd is { } end && Math.Abs(PositionFor(end) - position.X) <= 9)
        {
            dragTarget = DragTarget.LoopEnd;
        }
        else
        {
            dragTarget = DragTarget.Playhead;
            value = frame;
            SeekRequested?.Invoke(this, new ReplayTimelineSeekEventArgs(frame));
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var frame = FrameFor(e.GetPosition(this).X);
        hoverFrame = frame;
        if (dragTarget == DragTarget.Playhead)
        {
            value = frame;
            SeekRequested?.Invoke(this, new ReplayTimelineSeekEventArgs(frame));
        }
        else if (dragTarget == DragTarget.LoopStart && loopEnd is { } end)
        {
            loopStart = Math.Min(frame, Math.Max(0, end - 1));
            LoopRangeChanged?.Invoke(this, new ReplayTimelineLoopEventArgs(loopStart.Value, end));
        }
        else if (dragTarget == DragTarget.LoopEnd && loopStart is { } start)
        {
            loopEnd = Math.Max(frame, Math.Min(maximum, start + 1));
            LoopRangeChanged?.Invoke(this, new ReplayTimelineLoopEventArgs(start, loopEnd.Value));
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (dragTarget == DragTarget.None)
        {
            base.OnPointerReleased(e);
            return;
        }

        base.OnPointerReleased(e);
        dragTarget = DragTarget.None;
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!IsEnabled) return;

        var logicalIndex = e.TryGetPosition(this, out var position)
            ? FrameFor(position.X)
            : value;
        var request = new ReplayTimelineContextEventArgs(logicalIndex);
        ContextFrameRequested?.Invoke(this, request);
        e.Handled = request.Handled;
    }

    private void DrawTrack(
        DrawingContext context,
        double y,
        double fraction,
        IBrush? progressBrush,
        IBrush? trackBrush,
        double thickness)
    {
        var left = TrackInset;
        var right = Bounds.Width - TrackInset;
        context.DrawLine(new Pen(trackBrush, thickness), new Point(left, y), new Point(right, y));
        var progressX = left + ((right - left) * Math.Clamp(fraction, 0, 1));
        context.DrawLine(new Pen(progressBrush, thickness), new Point(left, y), new Point(progressX, y));
    }

    private void DrawHoverLabel(DrawingContext context, int frame, double x)
    {
        var text = new FormattedText(
            $"Frame {frame + 1:N0}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.SemiBold),
            11,
            PlayheadBrush);
        var width = text.Width + 12;
        var labelX = Math.Clamp(x - (width / 2), TrackInset, Math.Max(TrackInset, Bounds.Width - TrackInset - width));
        var rect = new Rect(labelX, 0, width, text.Height + 5);
        context.DrawRectangle(TrackBrush, new Pen(HoverTrackBrush, 1), rect, 2, 2);
        context.DrawText(text, new Point(rect.X + 6, rect.Y + 2));
    }

    private double PositionFor(int frame) =>
        TrackInset + ((Bounds.Width - (TrackInset * 2)) * Math.Clamp(frame, 0, maximum) / maximum);

    private int FrameFor(double x)
    {
        var width = Math.Max(1, Bounds.Width - (TrackInset * 2));
        var fraction = Math.Clamp((x - TrackInset) / width, 0, 1);
        return (int)Math.Round(fraction * maximum);
    }

    private enum DragTarget
    {
        None,
        Playhead,
        LoopStart,
        LoopEnd,
    }
}

public sealed class ReplayTimelineSeekEventArgs(int logicalIndex) : EventArgs
{
    public int LogicalIndex { get; } = logicalIndex;
}

public sealed class ReplayTimelineLoopEventArgs(int startLogicalIndex, int endLogicalIndex) : EventArgs
{
    public int StartLogicalIndex { get; } = startLogicalIndex;
    public int EndLogicalIndex { get; } = endLogicalIndex;
}

public sealed class ReplayTimelineContextEventArgs(int logicalIndex) : EventArgs
{
    public int LogicalIndex { get; } = logicalIndex;
    public bool Handled { get; set; }
}
