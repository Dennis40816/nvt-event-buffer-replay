using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Nvt.Replay.Analysis;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Avalonia.Controls;

public enum ReplayPaintMode
{
    HostState,
    ReportedFrame,
    Compare,
}

public sealed class ReplayPaintSurface : Control
{
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.Parse("#0A0D0F"));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#172025"));
    private static readonly IBrush HostBrush = new SolidColorBrush(Color.Parse("#C8F36C"));
    private static readonly IBrush ReportedBrush = new SolidColorBrush(Color.Parse("#58C7D9"));
    private static readonly IBrush BreakBrush = new SolidColorBrush(Color.Parse("#E2A65B"));
    private static readonly IBrush PalmBrush = new SolidColorBrush(Color.Parse("#D477B8"));
    private static readonly IBrush AlarmBrush = new SolidColorBrush(Color.Parse("#DD665E"));
    private IReadOnlyList<ReplayContact> reportedContacts = [];
    private IReadOnlyList<ReplayContact> hostContacts = [];
    private double extentX = 4095;
    private double extentY = 2160;
    private bool globalPalm;

    public ReplayPaintMode Mode { get; private set; } = ReplayPaintMode.Compare;

    public void SetMode(ReplayPaintMode mode)
    {
        Mode = mode;
        InvalidateVisual();
    }

    public void SetExtent(IEnumerable<CommonEventBufferFrame> frames)
    {
        var reported = frames.SelectMany(frame => frame.Fingers).Where(finger => finger.IsReported).ToArray();
        extentX = NiceExtent(reported.Select(finger => (double)finger.X).DefaultIfEmpty(1920).Max(), 1920);
        extentY = NiceExtent(reported.Select(finger => (double)finger.Y).DefaultIfEmpty(1080).Max(), 1080);
        InvalidateVisual();
    }

    public void Show(CommonReplaySnapshot snapshot)
    {
        reportedContacts = snapshot.ReportedContacts;
        hostContacts = snapshot.HostContacts;
        globalPalm = snapshot.GlobalPalm;
        InvalidateVisual();
    }

    public void Clear()
    {
        reportedContacts = [];
        hostContacts = [];
        globalPalm = false;
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

        if (Mode is ReplayPaintMode.ReportedFrame or ReplayPaintMode.Compare)
        {
            foreach (var contact in reportedContacts)
            {
                DrawContact(context, viewport, contact, reported: true);
            }
        }
        if (Mode is ReplayPaintMode.HostState or ReplayPaintMode.Compare)
        {
            foreach (var contact in hostContacts)
            {
                DrawContact(context, viewport, contact, reported: false);
            }
        }

        if (globalPalm)
        {
            context.DrawRectangle(null, new Pen(AlarmBrush, 4), viewport);
        }
    }

    private void DrawContact(DrawingContext context, Rect viewport, ReplayContact contact, bool reported)
    {
        var x = viewport.X + (contact.X / extentX * viewport.Width);
        var y = viewport.Y + (contact.Y / extentY * viewport.Height);
        var center = new Point(x, y);
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

    private static double NiceExtent(double maximum, double minimum)
    {
        var required = Math.Max(minimum, maximum * 1.08);
        return Math.Ceiling(required / 256) * 256;
    }
}
