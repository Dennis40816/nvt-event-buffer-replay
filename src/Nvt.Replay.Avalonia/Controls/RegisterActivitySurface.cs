using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>Draws register activity markers by source record index and handles pointer selection.</summary>
public sealed class RegisterActivitySurface : Control
{
    /// <summary>Identifies the styled property for the register activity track and default marker brush.</summary>
    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(TrackBrush));
    /// <summary>Identifies the styled property for the register read marker brush.</summary>
    public static readonly StyledProperty<IBrush?> ReadBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(ReadBrush));
    /// <summary>Identifies the styled property for the register write marker brush.</summary>
    public static readonly StyledProperty<IBrush?> WriteBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(WriteBrush));
    /// <summary>Identifies the styled property for the command and page-switch marker brush.</summary>
    public static readonly StyledProperty<IBrush?> CommandBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(CommandBrush));
    /// <summary>Identifies the styled property for the register reset marker brush.</summary>
    public static readonly StyledProperty<IBrush?> ResetBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(ResetBrush));
    /// <summary>Identifies the styled property for the ambiguous register activity marker brush.</summary>
    public static readonly StyledProperty<IBrush?> AmbiguousBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(AmbiguousBrush));
    /// <summary>Identifies the styled property for the selected activity and changed-read marker brush.</summary>
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty =
        AvaloniaProperty.Register<RegisterActivitySurface, IBrush?>(nameof(SelectionBrush));

    private IReadOnlyList<RegisterActivityEntry> activities = [];
    private long minimumIndex;
    private long maximumIndex = 1;
    private string? selectedStableId;

    /// <summary>Occurs when a left click selects the nearest register activity.</summary>
    public event EventHandler<RegisterActivitySelectedEventArgs>? ActivitySelected;

    /// <summary>Gets or sets the brush for the register activity track and default markers.</summary>
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    /// <summary>Gets or sets the brush for register read markers.</summary>
    public IBrush? ReadBrush { get => GetValue(ReadBrushProperty); set => SetValue(ReadBrushProperty, value); }
    /// <summary>Gets or sets the brush for register write markers.</summary>
    public IBrush? WriteBrush { get => GetValue(WriteBrushProperty); set => SetValue(WriteBrushProperty, value); }
    /// <summary>Gets or sets the brush for command and page-switch markers.</summary>
    public IBrush? CommandBrush { get => GetValue(CommandBrushProperty); set => SetValue(CommandBrushProperty, value); }
    /// <summary>Gets or sets the brush for register reset markers.</summary>
    public IBrush? ResetBrush { get => GetValue(ResetBrushProperty); set => SetValue(ResetBrushProperty, value); }
    /// <summary>Gets or sets the brush for ambiguous register activity markers.</summary>
    public IBrush? AmbiguousBrush { get => GetValue(AmbiguousBrushProperty); set => SetValue(AmbiguousBrushProperty, value); }
    /// <summary>Gets or sets the brush for selected activities and changed-read markers.</summary>
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    /// <summary>Sets the register activities and source record index range, then requests a redraw.</summary>
    public void SetActivities(IReadOnlyList<RegisterActivityEntry> value, long minRecordIndex, long maxRecordIndex)
    {
        activities = value;
        minimumIndex = minRecordIndex;
        maximumIndex = Math.Max(minRecordIndex + 1, maxRecordIndex);
        InvalidateVisual();
    }

    /// <summary>Sets the stable identifier of the selected source record and requests a redraw.</summary>
    public void SetSelected(string? stableId)
    {
        selectedStableId = stableId;
        InvalidateVisual();
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
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

/// <summary>Carries the source record selected from the register activity track.</summary>
public sealed class RegisterActivitySelectedEventArgs(SourceRecord record) : EventArgs
{
    /// <summary>Gets the source record selected from the register activity track.</summary>
    public SourceRecord Record { get; } = record;
}
