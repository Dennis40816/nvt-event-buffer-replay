using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class RegisterActivitySurface : Control
{
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(TrackBrush));
    public static readonly StyledProperty<IBrush?> ReadBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(ReadBrush));
    public static readonly StyledProperty<IBrush?> WriteBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(WriteBrush));
    public static readonly StyledProperty<IBrush?> CommandBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(CommandBrush));
    public static readonly StyledProperty<IBrush?> ResetBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(ResetBrush));
    public static readonly StyledProperty<IBrush?> AmbiguousBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(AmbiguousBrush));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(SelectionBrush));

    private IReadOnlyList<RegisterActivityEntry> activities = [];
    private long minimumIndex;
    private long maximumIndex = 1;
    private string? selectedStableId;

    public event EventHandler<RegisterActivitySelectedEventArgs>? ActivitySelected;

    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public IBrush? ReadBrush { get => GetValue(ReadBrushProperty); set => SetValue(ReadBrushProperty, value); }
    public IBrush? WriteBrush { get => GetValue(WriteBrushProperty); set => SetValue(WriteBrushProperty, value); }
    public IBrush? CommandBrush { get => GetValue(CommandBrushProperty); set => SetValue(CommandBrushProperty, value); }
    public IBrush? ResetBrush { get => GetValue(ResetBrushProperty); set => SetValue(ResetBrushProperty, value); }
    public IBrush? AmbiguousBrush { get => GetValue(AmbiguousBrushProperty); set => SetValue(AmbiguousBrushProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    public void SetActivities(IReadOnlyList<RegisterActivityEntry> value, long minRecordIndex, long maxRecordIndex)
    {
        activities = value;
        minimumIndex = minRecordIndex;
        maximumIndex = Math.Max(minRecordIndex + 1, maxRecordIndex);
        InvalidateVisual();
    }

    public void SetSelected(string? stableId)
    {
        selectedStableId = stableId;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 10 || Bounds.Height < 4) return;
        const double inset = 10;
        var y = Bounds.Height / 2;
        context.DrawLine(new Pen(TrackBrush, 1), new Point(inset, y), new Point(Bounds.Width - inset, y));
        foreach (var activity in activities)
        {
            var x = PositionFor(activity.Record.Index, inset);
            var selected = activity.Record.StableId == selectedStableId;
            var brush = selected ? SelectionBrush : BrushFor(activity.Kind);
            var halfHeight = selected ? 8d : activity.Kind is RegisterActivityKind.Reset or RegisterActivityKind.Command or RegisterActivityKind.PageSwitch ? 6d : 4d;
            context.DrawLine(new Pen(brush, selected ? 2.5 : 1.5), new Point(x, y - halfHeight), new Point(x, y + halfHeight));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || activities.Count == 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        const double inset = 10;
        var x = e.GetPosition(this).X;
        var nearest = activities.MinBy(activity => Math.Abs(PositionFor(activity.Record.Index, inset) - x));
        if (nearest is null) return;
        selectedStableId = nearest.Record.StableId;
        ActivitySelected?.Invoke(this, new RegisterActivitySelectedEventArgs(nearest.Record));
        InvalidateVisual();
        e.Handled = true;
    }

    private double PositionFor(long index, double inset)
    {
        var width = Math.Max(1, Bounds.Width - (inset * 2));
        var fraction = Math.Clamp((index - minimumIndex) / (double)(maximumIndex - minimumIndex), 0, 1);
        return inset + (width * fraction);
    }

    private IBrush? BrushFor(RegisterActivityKind kind) => kind switch
    {
        RegisterActivityKind.Read => ReadBrush,
        RegisterActivityKind.ChangedRead => SelectionBrush,
        RegisterActivityKind.Write => WriteBrush,
        RegisterActivityKind.Command => CommandBrush,
        RegisterActivityKind.Reset => ResetBrush,
        RegisterActivityKind.PageSwitch => CommandBrush,
        RegisterActivityKind.Ambiguous => AmbiguousBrush,
        _ => TrackBrush,
    };
}

public sealed class RegisterActivitySelectedEventArgs(SourceRecord record) : EventArgs
{
    public SourceRecord Record { get; } = record;
}
