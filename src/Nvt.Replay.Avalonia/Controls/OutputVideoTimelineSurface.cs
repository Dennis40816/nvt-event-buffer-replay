using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class OutputVideoTimelineSurface : Control
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
    private int? hoverPosition;
    private bool dragging;

    public event EventHandler<OutputVideoSeekEventArgs>? SeekRequested;

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? HoverTrackBrush { get => GetValue(HoverTrackBrushProperty); set => SetValue(HoverTrackBrushProperty, value); }
    public IBrush? ProgressBrush { get => GetValue(ProgressBrushProperty); set => SetValue(ProgressBrushProperty, value); }
    public IBrush? PlayheadBrush { get => GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }
    public IBrush? LabelSurfaceBrush { get => GetValue(LabelSurfaceBrushProperty); set => SetValue(LabelSurfaceBrushProperty, value); }
    public IBrush? LabelTextBrush { get => GetValue(LabelTextBrushProperty); set => SetValue(LabelTextBrushProperty, value); }

    public OutputVideoTimelineSurface()
    {
        Focusable = true;
    }

    public void SetFrameCount(int count, int framesPerSecond)
    {
        frameCount = Math.Max(0, count);
        frameRate = Math.Clamp(framesPerSecond, 1, 120);
        position = Math.Clamp(position, 0, Maximum);
        IsEnabled = frameCount > 0;
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

        var y = Bounds.Height - 10;
        var left = TrackInset;
        var right = Bounds.Width - TrackInset;
        var thickness = IsPointerOver || dragging || IsKeyboardFocusWithin ? 3 : 1.5;
        context.DrawLine(new Pen(IsPointerOver ? HoverTrackBrush : TrackBrush, thickness), new Point(left, y), new Point(right, y));

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
        if (!dragging) hoverPosition = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        dragging = true;
        SeekTo(FrameFor(e.GetPosition(this).X));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        hoverPosition = FrameFor(e.GetPosition(this).X);
        if (dragging) SeekTo(hoverPosition.Value);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
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

    private double PositionFor(int outputFrameIndex) =>
        TrackInset + ((Bounds.Width - (TrackInset * 2)) * (Maximum == 0 ? 0 : Math.Clamp(outputFrameIndex, 0, Maximum) / (double)Maximum));

    private int FrameFor(double x)
    {
        if (Maximum == 0) return 0;
        var width = Math.Max(1, Bounds.Width - (TrackInset * 2));
        var fraction = Math.Clamp((x - TrackInset) / width, 0, 1);
        return (int)Math.Round(fraction * Maximum);
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
}

public sealed class OutputVideoSeekEventArgs(int outputFrameIndex) : EventArgs
{
    public int OutputFrameIndex { get; } = outputFrameIndex;
}
