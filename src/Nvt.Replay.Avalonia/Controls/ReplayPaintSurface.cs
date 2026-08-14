using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class ReplayPaintSurface : Control
{
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#0A0D0F"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#172025"));
    private static readonly IBrush HostBrush = new SolidColorBrush(Color.Parse("#C8F36C"));
    private static readonly IBrush ReportedBrush = new SolidColorBrush(Color.Parse("#58C7D9"));
    private static readonly IBrush BreakBrush = new SolidColorBrush(Color.Parse("#E2A65B"));
    private static readonly IBrush PalmBrush = new SolidColorBrush(Color.Parse("#D477B8"));
    private static readonly IBrush AlarmBrush = new SolidColorBrush(Color.Parse("#DD665E"));
    private static readonly IBrush InvalidBrush = new SolidColorBrush(Color.Parse("#E56C6C"));
    private ReplayScene? scene;

    public ReplayRenderMode Mode { get; private set; } = ReplayRenderMode.Compare;

    public void SetMode(ReplayRenderMode mode)
    {
        Mode = mode;
        InvalidateVisual();
    }

    public void Show(ReplayScene value)
    {
        scene = value;
        InvalidateVisual();
    }

    public void Clear()
    {
        scene = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(SurfaceBrush, null, bounds);

        const double margin = 34;
        var viewport = new Rect(
            margin,
            margin,
            Math.Max(1, bounds.Width - (margin * 2)),
            Math.Max(1, bounds.Height - (margin * 2)));
        context.DrawRectangle(null, new Pen(GridBrush, 1), viewport);
        for (var division = 1; division < 8; division++)
        {
            var x = viewport.X + (viewport.Width * division / 8);
            var y = viewport.Y + (viewport.Height * division / 8);
            context.DrawLine(new Pen(GridBrush, 1), new Point(x, viewport.Y), new Point(x, viewport.Bottom));
            context.DrawLine(new Pen(GridBrush, 1), new Point(viewport.X, y), new Point(viewport.Right, y));
        }

        if (scene is null) return;
        if (Mode is ReplayRenderMode.ReportedFrame or ReplayRenderMode.Compare)
        {
            foreach (var contact in scene.ReportedContacts)
            {
                DrawContact(context, viewport, contact, scene.Extent, reported: true);
            }
        }
        if (Mode is ReplayRenderMode.HostState or ReplayRenderMode.Compare)
        {
            foreach (var contact in scene.HostContacts)
            {
                DrawContact(context, viewport, contact, scene.Extent, reported: false);
            }
        }

        if (scene.GlobalPalm)
        {
            context.DrawRectangle(null, new Pen(AlarmBrush, 4), viewport);
        }
    }

    private void DrawContact(DrawingContext context, Rect viewport, ReplayContact contact, ReplayExtent extent, bool reported)
    {
        var x = viewport.X + (contact.X / extent.MaximumX * viewport.Width);
        var y = viewport.Y + (contact.Y / extent.MaximumY * viewport.Height);
        var center = new Point(x, y);
        if (contact.Invalid)
        {
            const double half = 10;
            var pen = new Pen(InvalidBrush, 2);
            context.DrawLine(pen, new Point(x - half, y - half), new Point(x + half, y + half));
            context.DrawLine(pen, new Point(x + half, y - half), new Point(x - half, y + half));
            return;
        }
        var brush = contact.Type == TouchType.Palm
            ? PalmBrush
            : contact.Status == TouchStatus.Break
                ? BreakBrush
                : reported
                    ? ReportedBrush
                    : HostBrush;
        var radius = reported ? 13 : 8;
        if (contact.Type == TouchType.Palm)
        {
            var rect = new Rect(x - radius, y - radius, radius * 2, radius * 2);
            context.DrawRectangle(reported ? null : brush, new Pen(brush, reported ? 2 : 1), rect);
        }
        else
        {
            context.DrawEllipse(reported ? null : brush, new Pen(brush, reported ? 2 : 1), center, radius, radius);
        }
    }

}
