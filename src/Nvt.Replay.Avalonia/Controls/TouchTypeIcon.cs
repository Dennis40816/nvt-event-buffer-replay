using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class TouchTypeIcon : Control
{
    public static readonly StyledProperty<byte> ContactIdProperty =
        AvaloniaProperty.Register<TouchTypeIcon, byte>(nameof(ContactId), 1);
    public static readonly StyledProperty<TouchType> TouchTypeProperty =
        AvaloniaProperty.Register<TouchTypeIcon, TouchType>(nameof(TouchType), TouchType.Finger);
    public static readonly StyledProperty<bool> InvalidProperty =
        AvaloniaProperty.Register<TouchTypeIcon, bool>(nameof(Invalid));

    public byte ContactId
    {
        get => GetValue(ContactIdProperty);
        set => SetValue(ContactIdProperty, value);
    }

    public TouchType TouchType
    {
        get => GetValue(TouchTypeProperty);
        set => SetValue(TouchTypeProperty, value);
    }

    public bool Invalid
    {
        get => GetValue(InvalidProperty);
        set => SetValue(InvalidProperty, value);
    }

    static TouchTypeIcon() =>
        AffectsRender<TouchTypeIcon>(ContactIdProperty, TouchTypeProperty, InvalidProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var palette = ReplayVisualStyle.For(
            ActualThemeVariant == ThemeVariant.Light ? ReplayRenderTheme.Light : ReplayRenderTheme.Dark);
        var color = Invalid
            ? palette.Invalid
            : ReplayVisualStyle.ContactColor(palette, ContactId);
        var brush = new SolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(2, Math.Min(Bounds.Width, Bounds.Height) * 0.28);

        switch (TouchType)
        {
            case TouchType.Finger:
                context.DrawEllipse(brush, null, center, radius, radius);
                break;
            case TouchType.Glove:
                DrawPolygon(
                    context,
                    brush,
                    new Point(center.X, center.Y - radius),
                    new Point(center.X + radius, center.Y),
                    new Point(center.X, center.Y + radius),
                    new Point(center.X - radius, center.Y));
                break;
            case TouchType.Palm:
                context.DrawRectangle(
                    brush,
                    null,
                    new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2),
                    1.5,
                    1.5);
                break;
            default:
                DrawPolygon(
                    context,
                    brush,
                    new Point(center.X, center.Y - radius),
                    new Point(center.X + radius, center.Y + radius),
                    new Point(center.X - radius, center.Y + radius));
                break;
        }
    }

    private static void DrawPolygon(DrawingContext context, IBrush brush, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(points[0], isFilled: true);
        foreach (var point in points.Skip(1)) path.LineTo(point);
        path.EndFigure(isClosed: true);
        context.DrawGeometry(brush, null, geometry);
    }
}
