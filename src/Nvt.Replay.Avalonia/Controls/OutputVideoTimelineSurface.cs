using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class OutputVideoTimelineSurface : Control, ICustomHitTest
{
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> HoverTrackBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(HoverTrackBrush));
    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(ProgressBrush));
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(PlayheadBrush));
    public static readonly StyledProperty<IBrush?> LabelSurfaceBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(LabelSurfaceBrush));
    public static readonly StyledProperty<IBrush?> LabelTextBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(LabelTextBrush));

    private const double TrackInset = 4;
    private int frameCount;
    private int frameRate = 30;
    private int position;
    private int sourceFrameCount;
    private int rangeStart;
    private int rangeEnd;
    private int? hoverPosition;
    private DragTarget dragTarget;

    public event EventHandler<OutputVideoSeekEventArgs>? SeekRequested;
    public event EventHandler<OutputVideoRangeEventArgs>? RangeChanged;

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? HoverTrackBrush { get => GetValue(HoverTrackBrushProperty); set => SetValue(HoverTrackBrushProperty, value); }
    public IBrush? ProgressBrush { get => GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public IBrush? LabelSurfaceBrush { get => GetValue(LabelSurfaceBrushProperty); set => SetValue(LabelSurfaceBrushProperty, value); }
    public IBrush? LabelTextBrush { get => GetValue(LabelTextBrushProperty); set => SetValue(LabelTextBrushProperty, value); }
    public bool ShowRange { get; set; } = true;
    public int RangeStart => rangeStart;
    public int RangeEnd => rangeEnd;

    public OutputVideoTimelineSurface()
    {
        Focusable = true;
    }

    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public void SetFrameCount(int count, int framesPerSecond)
    {
        frameCount = Math.Max(0, count);
        frameRate = Math.Clamp(framesPerSecond, 1, 240);
        position = Math.Clamp(position, 0, Maximum);
        IsEnabled = frameCount > 0;
        InvalidateVisual();
    }

    public void SetSourceRange(int count, int start, int end)
    {
        sourceFrameCount = Math.Max(0, count);
        var maximum = SourceMaximum;
        rangeStart = Math.Clamp(Math.Min(start, end), 0, maximum);
        rangeEnd = Math.Clamp(Math.Max(start, end), rangeStart, maximum);
        InvalidateVisual();
    }

    public void SetPosition(int outputFrameIndex)
    {
        position = Math.Clamp(outputFrameIndex, 0, Maximum);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= TrackInset * 2 || Bounds.Height <= 1) return;

        var y = Bounds.Height / 2;
        var left = TrackInset;
        var right = Bounds.Width - TrackInset;
        var thickness = IsPointerOver || dragTarget != DragTarget.None || IsKeyboardFocusWithin ? 3 : 1.5;
        context.DrawLine(new Pen(IsPointerOver ? HoverTrackBrush : TrackBrush, thickness), new Point(left, y), new Point(right, y));
        if (ShowRange)
        {
            if (sourceFrameCount > 0)
            {
                var rangeStartX = SourcePositionFor(rangeStart);
                var rangeEndX = SourcePositionFor(rangeEnd);
                context.DrawLine(new Pen(HoverTrackBrush, 3), new Point(rangeStartX, y), new Point(rangeEndX, y));
                DrawRangeHandle(context, rangeStartX, y, pointsRight: true);
                DrawRangeHandle(context, rangeEndX, y, pointsRight: false);
            }
        }

        var playheadX = PositionFor(position);
        context.DrawLine(new Pen(ProgressBrush, thickness), new Point(left, y), new Point(playheadX, y));
        context.DrawLine(new Pen(PlayheadBrush, 2), new Point(playheadX, y - 9), new Point(playheadX, y + 8));
        context.DrawRectangle(PlayheadBrush, null, new Rect(playheadX - 3, y - 10, 6, 3), 1, 1);

        if (hoverPosition is { } hover && IsPointerOver)
            DrawHoverLabel(context, hover, PositionFor(hover));
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (dragTarget == DragTarget.None) hoverPosition = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var x = e.GetPosition(this).X;
        dragTarget = RangeTargetFor(x);
        if (dragTarget == DragTarget.None)
        {
            dragTarget = DragTarget.Seek;
            SeekTo(FrameFor(x));
        }
        else
        {
            MoveRangeHandle(SourceFrameFor(x));
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hoverPosition = FrameFor(e.GetPosition(this).X);
        if (dragTarget == DragTarget.Seek)
            SeekTo(hoverPosition.Value);
        else if (dragTarget is DragTarget.RangeStart or DragTarget.RangeEnd)
            MoveRangeHandle(SourceFrameFor(e.GetPosition(this).X));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var completed = dragTarget;
        dragTarget = DragTarget.None;
        e.Pointer.Capture(null);
        if (completed is DragTarget.RangeStart or DragTarget.RangeEnd)
            RangeChanged?.Invoke(this, new OutputVideoRangeEventArgs(rangeStart, rangeEnd));
        e.Handled = completed != DragTarget.None;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var target = e.Key switch
        {
            Key.Left => position - 1,
            Key.Right => position + 1,
            Key.Home => 0,
            Key.End => Maximum,
            _ => position,
        };
        if (target == position || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        SeekTo(target);
        e.Handled = true;
    }

    private void SeekTo(int outputFrameIndex)
    {
        position = Math.Clamp(outputFrameIndex, 0, Maximum);
        SeekRequested?.Invoke(this, new OutputVideoSeekEventArgs(position));
        InvalidateVisual();
    }

    private int Maximum => Math.Max(0, frameCount - 1);
    private int SourceMaximum => Math.Max(0, sourceFrameCount - 1);

    private double PositionFor(int outputFrameIndex) =>
        TrackInset + ((Bounds.Width - (TrackInset * 2)) * (Maximum == 0 ? 0 : Math.Clamp(outputFrameIndex, 0, Maximum) / (double)Maximum));

    private int FrameFor(double x)
    {
        if (Maximum == 0) return 0;
        var width = Math.Max(1, Bounds.Width - (TrackInset * 2));
        var fraction = Math.Clamp((x - TrackInset) / width, 0, 1);
        return (int)Math.Round(fraction * Maximum);
    }

    private double SourcePositionFor(int sourceFrameIndex) =>
        TrackInset + ((Bounds.Width - (TrackInset * 2)) * (SourceMaximum == 0 ? 0 : Math.Clamp(sourceFrameIndex, 0, SourceMaximum) / (double)SourceMaximum));

    private int SourceFrameFor(double x)
    {
        if (SourceMaximum == 0) return 0;
        var width = Math.Max(1, Bounds.Width - (TrackInset * 2));
        var fraction = Math.Clamp((x - TrackInset) / width, 0, 1);
        return (int)Math.Round(fraction * SourceMaximum);
    }

    private DragTarget RangeTargetFor(double x)
    {
        if (!ShowRange || sourceFrameCount == 0) return DragTarget.None;
        var startDistance = Math.Abs(x - SourcePositionFor(rangeStart));
        var endDistance = Math.Abs(x - SourcePositionFor(rangeEnd));
        const double hitRadius = 11;
        if (startDistance > hitRadius && endDistance > hitRadius) return DragTarget.None;
        return startDistance <= endDistance ? DragTarget.RangeStart : DragTarget.RangeEnd;
    }

    private void MoveRangeHandle(int sourceFrameIndex)
    {
        if (dragTarget == DragTarget.RangeStart)
            rangeStart = Math.Clamp(sourceFrameIndex, 0, rangeEnd);
        else if (dragTarget == DragTarget.RangeEnd)
            rangeEnd = Math.Clamp(sourceFrameIndex, rangeStart, SourceMaximum);
        InvalidateVisual();
    }

    private void DrawRangeHandle(DrawingContext context, double x, double y, bool pointsRight)
    {
        var pen = new Pen(PlayheadBrush, 1.5);
        context.DrawLine(pen, new Point(x, y - 7), new Point(x, y + 7));
        var direction = pointsRight ? 1 : -1;
        context.DrawLine(pen, new Point(x, y - 7), new Point(x + (direction * 5), y - 4));
        context.DrawLine(pen, new Point(x, y + 7), new Point(x + (direction * 5), y + 4));
    }

    private void DrawHoverLabel(DrawingContext context, int outputFrameIndex, double x)
    {
        var time = TimeSpan.FromSeconds(outputFrameIndex / (double)frameRate);
        var label = time.TotalHours >= 1
            ? time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : time.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.SemiBold),
            11,
            LabelTextBrush);
        var width = text.Width + 12;
        var labelX = Math.Clamp(x - (width / 2), TrackInset, Math.Max(TrackInset, Bounds.Width - TrackInset - width));
        var rect = new Rect(labelX, 0, width, text.Height + 5);
        context.DrawRectangle(LabelSurfaceBrush, new Pen(HoverTrackBrush, 1), rect, 3, 3);
        context.DrawText(text, new Point(rect.X + 6, rect.Y + 2));
    }

    private enum DragTarget
    {
        None,
        Seek,
        RangeStart,
        RangeEnd,
    }
}

public sealed class OutputVideoSeekEventArgs(int outputFrameIndex) : EventArgs
{
    public int OutputFrameIndex { get; } = outputFrameIndex;
}

public sealed class OutputVideoRangeEventArgs(int startLogicalIndex, int endLogicalIndex) : EventArgs
{
    public int StartLogicalIndex { get; } = startLogicalIndex;
    public int EndLogicalIndex { get; } = endLogicalIndex;
}
