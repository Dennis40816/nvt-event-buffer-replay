using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>Draws the replay timeline with its playhead, progress tracks, loop range and markers, and handles seek input.</summary>
public sealed class ReplayTimelineSurface : Control, ICustomHitTest
{
    /// <summary>Identifies the styled property for the timeline track background brush.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(TrackBrush));
    /// <summary>Identifies the styled property for the track brush used while the pointer is over the timeline.</summary>
    public static readonly StyledProperty<IBrush?> HoverTrackBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(HoverTrackBrush));
    /// <summary>Identifies the styled property for the physical progress track brush.</summary>
    public static readonly StyledProperty<IBrush?> PhysicalBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(PhysicalBrush));
    /// <summary>Identifies the styled property for the logical progress track brush.</summary>
    public static readonly StyledProperty<IBrush?> LogicalBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(LogicalBrush));
    /// <summary>Identifies the styled property for the evidence progress track brush.</summary>
    public static readonly StyledProperty<IBrush?> EvidenceBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(EvidenceBrush));
    /// <summary>Identifies the styled property for the playhead and hover label text brush.</summary>
    public static readonly StyledProperty<IBrush?> PlayheadBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(PlayheadBrush));
    /// <summary>Identifies the styled property for the enabled loop boundary brush.</summary>
    public static readonly StyledProperty<IBrush?> LoopBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(LoopBrush));
    /// <summary>Identifies the styled property for the enabled loop range fill brush.</summary>
    public static readonly StyledProperty<IBrush?> LoopSurfaceBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(LoopSurfaceBrush));
    /// <summary>Identifies the styled property for markers outside the current playhead frame.</summary>
    public static readonly StyledProperty<IBrush?> MarkerBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(MarkerBrush));
    /// <summary>Identifies the styled property for markers at the current playhead frame.</summary>
    public static readonly StyledProperty<IBrush?> ActiveMarkerBrushProperty =
        AvaloniaProperty.Register<ReplayTimelineSurface, IBrush?>(nameof(ActiveMarkerBrush));

    private const double TrackInset = 5;
    private int _maximum = 1;
    private int _value;
    private double _physicalFraction;
    private double _evidenceFraction;
    private int? _loopStart;
    private int? _loopEnd;
    private bool _loopEnabled;
    private int[] _markerFrames = [];
    private int? _hoverFrame;
    private DragTarget _dragTarget;

    /// <summary>Occurs when pointer input requests a seek to a logical replay frame.</summary>
    public event EventHandler<ReplayTimelineSeekEventArgs>? SeekRequested;
    /// <summary>Occurs when dragging a loop boundary changes the logical frame range.</summary>
    public event EventHandler<ReplayTimelineLoopEventArgs>? LoopRangeChanged;
    /// <summary>Occurs when a context request targets a logical replay frame.</summary>
    public event EventHandler<ReplayTimelineContextEventArgs>? ContextFrameRequested;

    /// <summary>Creates a timeline surface and subscribes to context requests for replay frames.</summary>
    public ReplayTimelineSurface()
    {
        ContextRequested += OnContextRequested;
    }

    /// <summary>Gets or sets the timeline track background and hover label fill brush.</summary>
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    /// <summary>Gets or sets the hovered track, disabled loop boundary, and hover label border brush.</summary>
    public IBrush? HoverTrackBrush { get => GetValue(HoverTrackBrushProperty); set => SetValue(HoverTrackBrushProperty, value); }
    /// <summary>Gets or sets the brush for physical progress shown when the timeline is expanded.</summary>
    public IBrush? PhysicalBrush { get => GetValue(PhysicalBrushProperty); set => SetValue(PhysicalBrushProperty, value); }
    /// <summary>Gets or sets the brush for logical progress up to the playhead.</summary>
    public IBrush? LogicalBrush { get => GetValue(LogicalBrushProperty); set => SetValue(LogicalBrushProperty, value); }
    /// <summary>Gets or sets the brush for evidence progress shown when the timeline is expanded.</summary>
    public IBrush? EvidenceBrush { get => GetValue(EvidenceBrushProperty); set => SetValue(EvidenceBrushProperty, value); }
    /// <summary>Gets or sets the brush for the playhead and hover label text.</summary>
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    /// <summary>Gets or sets the brush for the enabled loop range boundaries.</summary>
    public IBrush? LoopBrush { get => GetValue(LoopBrushProperty); set => SetValue(LoopBrushProperty, value); }
    /// <summary>Gets or sets the brush filling the enabled loop range.</summary>
    public IBrush? LoopSurfaceBrush { get => GetValue(LoopSurfaceBrushProperty); set => SetValue(LoopSurfaceBrushProperty, value); }
    /// <summary>Gets or sets the brush for markers outside the current playhead frame.</summary>
    public IBrush? MarkerBrush { get => GetValue(MarkerBrushProperty); set => SetValue(MarkerBrushProperty, value); }
    /// <summary>Gets or sets the brush for markers at the current playhead frame.</summary>
    public IBrush? ActiveMarkerBrush { get => GetValue(ActiveMarkerBrushProperty); set => SetValue(ActiveMarkerBrushProperty, value); }
    /// <summary>Gets or sets whether the timeline hides the physical and evidence progress tracks.</summary>
    public bool Compact { get; set; } = true;
    internal IReadOnlyList<int> MarkerFrames => _markerFrames;

    /// <inheritdoc />
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <summary>Sets the last logical index of the timeline and clamps the playhead and marker frames to it.</summary>
    public void SetMaximum(int value)
    {
        _maximum = Math.Max(1, value);
        _value = Math.Clamp(_value, 0, _maximum);
        _markerFrames = _markerFrames.Select(frame => Math.Clamp(frame, 0, _maximum)).Distinct().Order().ToArray();
        InvalidateVisual();
    }

    /// <summary>Sets the playhead to a logical index, clamped to the timeline maximum.</summary>
    public void SetPosition(int logicalIndex)
    {
        _value = Math.Clamp(logicalIndex, 0, _maximum);
        InvalidateVisual();
    }

    /// <summary>Sets the physical and evidence progress fractions, clamped to 0 through 1.</summary>
    public void SetSupportingProgress(double physical, double evidence)
    {
        _physicalFraction = Math.Clamp(physical, 0, 1);
        _evidenceFraction = Math.Clamp(evidence, 0, 1);
        InvalidateVisual();
    }

    /// <summary>Sets the playhead and the physical and evidence progress fractions in one step.</summary>
    public void SetFrameState(int logicalIndex, double physical, double evidence)
    {
        _value = Math.Clamp(logicalIndex, 0, _maximum);
        _physicalFraction = Math.Clamp(physical, 0, 1);
        _evidenceFraction = Math.Clamp(evidence, 0, 1);
        InvalidateVisual();
    }

    /// <summary>Sets the loop range and whether it is enabled; the bounds are clamped to the timeline.</summary>
    public void SetLoopRange(int? start, int? end, bool enabled)
    {
        _loopStart = start is null ? null : Math.Clamp(start.Value, 0, _maximum);
        _loopEnd = end is null ? null : Math.Clamp(end.Value, 0, _maximum);
        _loopEnabled = enabled && _loopStart is not null && _loopEnd is not null;
        InvalidateVisual();
    }

    /// <summary>Replaces the marker frames with the distinct, ordered and clamped values of the given frames.</summary>
    public void SetMarkerFrames(IEnumerable<int> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _markerFrames = frames
            .Select(frame => Math.Clamp(frame, 0, _maximum))
            .Distinct()
            .Order()
            .ToArray();
        InvalidateVisual();
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= TrackInset * 2 || Bounds.Height <= 1) return;

        var logicalY = Bounds.Height * 0.5;
        var trackThickness = IsPointerOver || _dragTarget != DragTarget.None ? 2.5 : 1;
        var trackBrush = IsPointerOver ? HoverTrackBrush : TrackBrush;
        var left = TrackInset;
        var right = Bounds.Width - TrackInset;

        if (!Compact)
        {
            DrawTrack(context, Bounds.Height * 0.125, _physicalFraction, PhysicalBrush, trackBrush, trackThickness);
            DrawTrack(context, Bounds.Height * 0.875, _evidenceFraction, EvidenceBrush, trackBrush, trackThickness);
        }
        DrawTrack(context, logicalY, _maximum == 0 ? 0 : _value / (double)_maximum, LogicalBrush, trackBrush, trackThickness);

        if (_loopStart is { } start && _loopEnd is { } end)
        {
            var startX = PositionFor(start);
            var endX = PositionFor(end);
            if (endX < startX) (startX, endX) = (endX, startX);
            if (_loopEnabled)
            {
                var selection = new Rect(startX, logicalY - 8, Math.Max(1, endX - startX), 16);
                context.DrawRectangle(LoopSurfaceBrush, null, selection, 2, 2);
            }
            var loopPen = new Pen(_loopEnabled ? LoopBrush : HoverTrackBrush, _loopEnabled ? 2.5 : 1.5);
            context.DrawLine(loopPen, new Point(startX, logicalY - 10), new Point(startX, logicalY + 10));
            context.DrawLine(loopPen, new Point(startX, logicalY - 10), new Point(startX + 6, logicalY - 10));
            context.DrawLine(loopPen, new Point(startX, logicalY + 10), new Point(startX + 6, logicalY + 10));
            context.DrawLine(loopPen, new Point(endX, logicalY - 10), new Point(endX, logicalY + 10));
            context.DrawLine(loopPen, new Point(endX - 6, logicalY - 10), new Point(endX, logicalY - 10));
            context.DrawLine(loopPen, new Point(endX - 6, logicalY + 10), new Point(endX, logicalY + 10));
        }

        foreach (var markerFrame in _markerFrames)
        {
            var x = PositionFor(markerFrame);
            var brush = markerFrame == _value ? ActiveMarkerBrush : MarkerBrush;
            context.DrawLine(new Pen(brush, 1.5), new Point(x, 2), new Point(x, logicalY - 1));
            var flag = new StreamGeometry();
            using (var path = flag.Open())
            {
                path.BeginFigure(new Point(x, 2), isFilled: true);
                path.LineTo(new Point(x + 7, 5));
                path.LineTo(new Point(x, 8));
                path.EndFigure(isClosed: true);
            }
            context.DrawGeometry(brush, null, flag);
        }

        var playheadX = Math.Clamp(PositionFor(_value), left, right);
        var playheadPen = new Pen(PlayheadBrush, IsPointerOver ? 2 : 1.5);
        context.DrawLine(playheadPen, new Point(playheadX, 2), new Point(playheadX, Bounds.Height - 2));
        context.DrawLine(playheadPen, new Point(playheadX - 3, 2), new Point(playheadX + 3, 2));

        if (_hoverFrame is { } frame && IsPointerOver)
            DrawHoverLabel(context, frame, PositionFor(frame));
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
        if (_dragTarget == DragTarget.None) _hoverFrame = null;
        InvalidateVisual();
    }

    /// <inheritdoc />
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
        if (_loopEnabled && _loopStart is { } start && Math.Abs(PositionFor(start) - position.X) <= 9)
        {
            _dragTarget = DragTarget.LoopStart;
        }
        else if (_loopEnabled && _loopEnd is { } end && Math.Abs(PositionFor(end) - position.X) <= 9)
        {
            _dragTarget = DragTarget.LoopEnd;
        }
        else
        {
            _dragTarget = DragTarget.Playhead;
            _value = frame;
            SeekRequested?.Invoke(this, new ReplayTimelineSeekEventArgs(frame));
        }
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var frame = FrameFor(e.GetPosition(this).X);
        _hoverFrame = frame;
        if (_dragTarget == DragTarget.Playhead)
        {
            _value = frame;
            SeekRequested?.Invoke(this, new ReplayTimelineSeekEventArgs(frame, isContinuous: true));
        }
        else if (_dragTarget == DragTarget.LoopStart && _loopEnd is { } end)
        {
            _loopStart = Math.Min(frame, Math.Max(0, end - 1));
            LoopRangeChanged?.Invoke(this, new ReplayTimelineLoopEventArgs(_loopStart.Value, end));
        }
        else if (_dragTarget == DragTarget.LoopEnd && _loopStart is { } start)
        {
            _loopEnd = Math.Max(frame, Math.Min(_maximum, start + 1));
            LoopRangeChanged?.Invoke(this, new ReplayTimelineLoopEventArgs(start, _loopEnd.Value));
        }
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragTarget == DragTarget.None)
        {
            base.OnPointerReleased(e);
            return;
        }

        base.OnPointerReleased(e);
        if (_dragTarget == DragTarget.Playhead)
        {
            var frame = FrameFor(e.GetPosition(this).X);
            _value = frame;
            SeekRequested?.Invoke(this, new ReplayTimelineSeekEventArgs(frame));
        }
        _dragTarget = DragTarget.None;
        if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        e.Handled = true;
        InvalidateVisual();
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!IsEnabled) return;

        var logicalIndex = e.TryGetPosition(this, out var position)
            ? FrameFor(position.X)
            : _value;
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
        TrackInset + ((Bounds.Width - (TrackInset * 2)) * Math.Clamp(frame, 0, _maximum) / _maximum);

    private int FrameFor(double x)
    {
        var width = Math.Max(1, Bounds.Width - (TrackInset * 2));
        var fraction = Math.Clamp((x - TrackInset) / width, 0, 1);
        return (int)Math.Round(fraction * _maximum);
    }

    private enum DragTarget
    {
        None,
        Playhead,
        LoopStart,
        LoopEnd,
    }
}

/// <summary>Describes a requested logical replay frame and whether the seek occurs during a drag.</summary>
public sealed class ReplayTimelineSeekEventArgs(int logicalIndex, bool isContinuous = false) : EventArgs
{
    /// <summary>Gets the logical replay frame index requested by the seek.</summary>
    public int LogicalIndex { get; } = logicalIndex;
    /// <summary>Gets whether the seek was requested while dragging the playhead.</summary>
    public bool IsContinuous { get; } = isContinuous;
}

/// <summary>Describes the logical frame boundaries produced by dragging a loop handle.</summary>
public sealed class ReplayTimelineLoopEventArgs(int startLogicalIndex, int endLogicalIndex) : EventArgs
{
    /// <summary>Gets the logical replay frame index of the loop start.</summary>
    public int StartLogicalIndex { get; } = startLogicalIndex;
    /// <summary>Gets the logical replay frame index of the loop end.</summary>
    public int EndLogicalIndex { get; } = endLogicalIndex;
}

/// <summary>Describes a context request for a logical replay frame and whether it was handled.</summary>
public sealed class ReplayTimelineContextEventArgs(int logicalIndex) : EventArgs
{
    /// <summary>Gets the logical replay frame index targeted by the context request.</summary>
    public int LogicalIndex { get; } = logicalIndex;
    /// <summary>Gets or sets whether the frame context request was handled by a subscriber.</summary>
    public bool Handled { get; set; }
}
