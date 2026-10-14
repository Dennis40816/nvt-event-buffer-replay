using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>Draws the output video timeline with its playhead and source range, and handles seek and range input.</summary>
public sealed class OutputVideoTimelineSurface : Control, ICustomHitTest
{
    /// <summary>Identifies the styled property for the normal timeline track brush.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(TrackBrush));
    /// <summary>Identifies the styled property for the hovered track, selected range, and time label border brush.</summary>
    public static readonly StyledProperty<IBrush?> HoverTrackBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(HoverTrackBrush));
    /// <summary>Identifies the styled property for the played track segment and playhead fill brush.</summary>
    public static readonly StyledProperty<IBrush?> ProgressBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(ProgressBrush));
    /// <summary>Identifies the styled property for the playhead outline and range handle brush.</summary>
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(PlayheadBrush));
    /// <summary>Identifies the styled property for the hover time label background brush.</summary>
    public static readonly StyledProperty<IBrush?> LabelSurfaceBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(LabelSurfaceBrush));
    /// <summary>Identifies the styled property for the hover time label text brush.</summary>
    public static readonly StyledProperty<IBrush?> LabelTextBrushProperty =
        AvaloniaProperty.Register<OutputVideoTimelineSurface, IBrush?>(nameof(LabelTextBrush));

    private const double TrackInset = 4;
    private int _frameCount;
    private int _frameRate = 30;
    private int _position;
    private int _sourceFrameCount;
    private int _rangeStart;
    private int _rangeEnd;
    private int? _hoverPosition;
    private DragTarget _dragTarget;

    /// <summary>Occurs when pointer or keyboard input requests an output frame position.</summary>
    public event EventHandler<OutputVideoSeekEventArgs>? SeekRequested;
    /// <summary>Occurs when dragging a source range handle ends.</summary>
    public event EventHandler<OutputVideoRangeEventArgs>? RangeChanged;

    /// <summary>Gets or sets the brush for the normal timeline track.</summary>
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    /// <summary>Gets or sets the brush for the hovered track, selected range, and time label border.</summary>
    public IBrush? HoverTrackBrush { get => GetValue(HoverTrackBrushProperty); set => SetValue(HoverTrackBrushProperty, value); }
    /// <summary>Gets or sets the brush for the played track segment and playhead fill.</summary>
    public IBrush? ProgressBrush { get => GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    /// <summary>Gets or sets the brush for the playhead outline and source range handles.</summary>
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    /// <summary>Gets or sets the background brush for the hover time label.</summary>
    public IBrush? LabelSurfaceBrush { get => GetValue(LabelSurfaceBrushProperty); set => SetValue(LabelSurfaceBrushProperty, value); }
    /// <summary>Gets or sets the text brush for the hover time label.</summary>
    public IBrush? LabelTextBrush { get => GetValue(LabelTextBrushProperty); set => SetValue(LabelTextBrushProperty, value); }
    /// <summary>Gets or sets whether the source range and its drag handles are displayed.</summary>
    public bool ShowRange { get; set; } = true;
    /// <summary>Gets the first source frame of the selected range.</summary>
    public int RangeStart => _rangeStart;
    /// <summary>Gets the last source frame of the selected range.</summary>
    public int RangeEnd => _rangeEnd;

    /// <summary>Creates a timeline surface that can receive keyboard focus.</summary>
    public OutputVideoTimelineSurface()
    {
        Focusable = true;
    }

    /// <inheritdoc />
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Sets the number of output frames and the frame rate, and clamps the playhead.</summary>
    public void SetFrameCount(int count, int framesPerSecond)
    {
        _frameCount = Math.Max(0, count);
        _frameRate = Math.Clamp(framesPerSecond, 1, 240);
        _position = Math.Clamp(_position, 0, Maximum);
        IsEnabled = _frameCount > 0;
        InvalidateVisual();
    }

    /// <summary>Sets the source frame count and the selected range, clamped to the source.</summary>
    public void SetSourceRange(int count, int start, int end)
    {
        _sourceFrameCount = Math.Max(0, count);
        var maximum = SourceMaximum;
        _rangeStart = Math.Clamp(Math.Min(start, end), 0, maximum);
        _rangeEnd = Math.Clamp(Math.Max(start, end), _rangeStart, maximum);
        InvalidateVisual();
    }

    /// <summary>Moves the playhead to an output frame, clamped to the timeline.</summary>
    public void SetPosition(int outputFrameIndex)
    {
        _position = Math.Clamp(outputFrameIndex, 0, Maximum);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= TrackInset * 2 || Bounds.Height <= 1) return;

        var y = Bounds.Height / 2;
        var left = TrackInset;
        var right = Bounds.Width - TrackInset;
        var thickness = IsPointerOver || _dragTarget != DragTarget.None || IsKeyboardFocusWithin ? 6 : 4;
        context.DrawLine(new Pen(IsPointerOver ? HoverTrackBrush : TrackBrush, thickness), new Point(left, y), new Point(right, y));
        if (ShowRange)
        {
            if (_sourceFrameCount > 0)
            {
                var rangeStartX = SourcePositionFor(_rangeStart);
                var rangeEndX = SourcePositionFor(_rangeEnd);
                context.DrawLine(new Pen(HoverTrackBrush, 4), new Point(rangeStartX, y), new Point(rangeEndX, y));
                DrawRangeHandle(context, rangeStartX, y, pointsRight: true);
                DrawRangeHandle(context, rangeEndX, y, pointsRight: false);
            }
        }

        var playheadX = PositionFor(_position);
        context.DrawLine(new Pen(ProgressBrush, thickness), new Point(left, y), new Point(playheadX, y));
        context.DrawEllipse(ProgressBrush, new Pen(PlayheadBrush, 1), new Point(playheadX, y), 7, 7);

        if (_hoverPosition is { } hover && IsPointerOver)
            DrawHoverLabel(context, hover, PositionFor(hover));
    }

    /// <inheritdoc />
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_dragTarget == DragTarget.None) _hoverPosition = null;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        var x = e.GetPosition(this).X;
        _dragTarget = RangeTargetFor(x);
        if (_dragTarget == DragTarget.None)
        {
            _dragTarget = DragTarget.Seek;
            SeekTo(FrameFor(x));
        }
        else
        {
            MoveRangeHandle(SourceFrameFor(x));
        }
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverPosition = FrameFor(e.GetPosition(this).X);
        if (_dragTarget == DragTarget.Seek)
            SeekTo(_hoverPosition.Value);
        else if (_dragTarget is DragTarget.RangeStart or DragTarget.RangeEnd)
            MoveRangeHandle(SourceFrameFor(e.GetPosition(this).X));
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var completed = _dragTarget;
        _dragTarget = DragTarget.None;
        e.Pointer.Capture(null);
        if (completed is DragTarget.RangeStart or DragTarget.RangeEnd)
            RangeChanged?.Invoke(this, new OutputVideoRangeEventArgs(_rangeStart, _rangeEnd));
        e.Handled = completed != DragTarget.None;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var target = e.Key switch
        {
            Key.Left => _position - 1,
            Key.Right => _position + 1,
            Key.Home => 0,
            Key.End => Maximum,
            _ => _position,
        };
        if (target == _position || e.Key is not (Key.Left or Key.Right or Key.Home or Key.End)) return;
        SeekTo(target);
        e.Handled = true;
    }

    private void SeekTo(int outputFrameIndex)
    {
        _position = Math.Clamp(outputFrameIndex, 0, Maximum);
        SeekRequested?.Invoke(this, new OutputVideoSeekEventArgs(_position));
        InvalidateVisual();
    }

    private int Maximum => Math.Max(0, _frameCount - 1);
    private int SourceMaximum => Math.Max(0, _sourceFrameCount - 1);

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
        if (!ShowRange || _sourceFrameCount == 0) return DragTarget.None;
        var startDistance = Math.Abs(x - SourcePositionFor(_rangeStart));
        var endDistance = Math.Abs(x - SourcePositionFor(_rangeEnd));
        const double hitRadius = 11;
        if (startDistance > hitRadius && endDistance > hitRadius) return DragTarget.None;
        return startDistance <= endDistance ? DragTarget.RangeStart : DragTarget.RangeEnd;
    }

    private void MoveRangeHandle(int sourceFrameIndex)
    {
        if (_dragTarget == DragTarget.RangeStart)
            _rangeStart = Math.Clamp(sourceFrameIndex, 0, _rangeEnd);
        else if (_dragTarget == DragTarget.RangeEnd)
            _rangeEnd = Math.Clamp(sourceFrameIndex, _rangeStart, SourceMaximum);
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
        var time = TimeSpan.FromSeconds(outputFrameIndex / (double)_frameRate);
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

/// <summary>Carries the output frame index requested by timeline input.</summary>
public sealed class OutputVideoSeekEventArgs(int outputFrameIndex) : EventArgs
{
    /// <summary>Gets the output frame index requested by timeline input.</summary>
    public int OutputFrameIndex { get; } = outputFrameIndex;
}

/// <summary>Carries the start and end source frame indices reported after a range handle drag.</summary>
public sealed class OutputVideoRangeEventArgs(int startLogicalIndex, int endLogicalIndex) : EventArgs
{
    /// <summary>Gets the source frame index at the start of the selected range.</summary>
    public int StartLogicalIndex { get; } = startLogicalIndex;
    /// <summary>Gets the source frame index at the end of the selected range.</summary>
    public int EndLogicalIndex { get; } = endLogicalIndex;
}
