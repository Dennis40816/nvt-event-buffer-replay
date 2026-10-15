using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>The brushes that the legend uses, taken from the active theme palette.</summary>
internal readonly record struct ReplayLegendBrushes(
    IBrush Surface,
    IBrush HoverSurface,
    IBrush Border,
    IBrush AxisLabel,
    IBrush Text);

/// <summary>Draws the contact legend of a replay scene and records its bounds for hit testing.</summary>
internal sealed class ReplayLegendPainter(PaintOverlayState overlay)
{
    private readonly PaintOverlayState _overlay = overlay;

    /// <summary>Draws the legend in <paramref name="viewport"/> and updates the legend bounds in the overlay state.</summary>
    public void Draw(
        DrawingContext context,
        Rect viewport,
        ReplayScene scene,
        ReplayRenderMode mode,
        ReplayLegendBrushes brushes,
        Func<byte, Color> contactColor)
    {
        if (!_overlay.LegendVisible)
        {
            _overlay.LegendBounds = null;
            return;
        }

        var entries = LegendEntries(scene);
        var activeContacts = ActiveContacts(scene, mode);
        if (entries.Length == 0)
        {
            _overlay.LegendBounds = null;
            return;
        }

        if (_overlay.LegendCollapsed)
        {
            DrawCollapsedLegend(context, viewport, activeContacts, brushes);
            return;
        }

        DrawExpandedLegend(context, viewport, entries, brushes, contactColor);
    }

    internal static string TypeLabel(TouchType type) => type switch
    {
        TouchType.Finger => "Finger",
        TouchType.Glove => "Glove",
        TouchType.Palm => "Palm",
        _ => "Other",
    };

    private static (byte Id, TouchType Type)[] LegendEntries(ReplayScene scene) =>
        scene.ReportedContacts
            .Concat(scene.HostContacts)
            .Select(contact => (contact.Id, contact.Type))
            .Concat(scene.ContactTrails.Select(trail => (trail.Id, trail.Type)))
            .GroupBy(entry => entry.Id)
            .Select(group => group.First())
            .OrderBy(entry => entry.Id)
            .Take(10)
            .ToArray();

    private static ReplayContact[] ActiveContacts(ReplayScene scene, ReplayRenderMode mode) =>
        (mode switch
        {
            ReplayRenderMode.HostState => scene.HostContacts,
            ReplayRenderMode.ReportedFrame => scene.ReportedContacts,
            _ => scene.ReportedContacts.Concat(scene.HostContacts).ToArray(),
        })
            .Where(contact => contact.IsActive)
            .GroupBy(contact => contact.Id)
            .Select(group => group.First())
            .OrderBy(contact => contact.Id)
            .Take(10)
            .ToArray();

    private void DrawExpandedLegend(
        DrawingContext context,
        Rect viewport,
        (byte Id, TouchType Type)[] entries,
        ReplayLegendBrushes brushes,
        Func<byte, Color> contactColor)
    {
        const double headerHeight = 28;
        const double itemHeight = 24;
        const double bottomPadding = 7;
        var legendRect = PositionLegend(
            viewport,
            new Size(150, headerHeight + (entries.Length * itemHeight) + bottomPadding));
        _overlay.LegendBounds = legendRect;
        context.DrawRectangle(
            _overlay.LegendHovered ? brushes.HoverSurface : brushes.Surface,
            new Pen(brushes.Border, 1),
            legendRect,
            3,
            3);

        var heading = new FormattedText(
            "CONTACTS",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            12.5,
            brushes.AxisLabel);
        var headingBounds = new Rect(legendRect.X + 10, legendRect.Y, legendRect.Width - 20, headerHeight);
        context.DrawText(heading, new Point(headingBounds.X, CenteredTextY(headingBounds, heading)));

        for (var index = 0; index < entries.Length; index++)
        {
            var rowBounds = new Rect(
                legendRect.X + 10,
                legendRect.Y + headerHeight + (index * itemHeight),
                legendRect.Width - 20,
                itemHeight);
            DrawLegendRow(context, rowBounds, entries[index], brushes, contactColor);
        }
    }

    private static void DrawLegendRow(
        DrawingContext context,
        Rect rowBounds,
        (byte Id, TouchType Type) entry,
        ReplayLegendBrushes brushes,
        Func<byte, Color> contactColor)
    {
        var color = contactColor(entry.Id);
        var brush = new SolidColorBrush(color);
        context.DrawEllipse(brush, null, new Point(rowBounds.X + 4, rowBounds.Center.Y), 3.5, 3.5);
        var text = new FormattedText(
            $"#{entry.Id}",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.SemiBold),
            13,
            brushes.Text);
        context.DrawText(text, new Point(rowBounds.X + 11, CenteredTextY(rowBounds, text)));
        DrawLegendType(
            context,
            new Rect(rowBounds.X + 48, rowBounds.Y, rowBounds.Width - 48, rowBounds.Height),
            entry.Type,
            TypeLabel(entry.Type),
            brushes);
    }

    private void DrawCollapsedLegend(
        DrawingContext context,
        Rect viewport,
        IReadOnlyList<ReplayContact> activeContacts,
        ReplayLegendBrushes brushes)
    {
        var summary = ContactSummary(activeContacts);
        var text = new FormattedText(
            summary,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Variable", FontStyle.Normal, FontWeight.SemiBold),
            13,
            brushes.Text);
        var legendRect = PositionLegend(viewport, new Size(text.Width + 39, 32));
        _overlay.LegendBounds = legendRect;
        context.DrawRectangle(
            _overlay.LegendHovered ? brushes.HoverSurface : brushes.Surface,
            new Pen(brushes.Border, 1),
            legendRect,
            15,
            15);
        context.DrawEllipse(brushes.AxisLabel, null, new Point(legendRect.X + 13, legendRect.Center.Y), 3.5, 3.5);
        var textBounds = new Rect(legendRect.X + 25, legendRect.Y, legendRect.Width - 31, legendRect.Height);
        context.DrawText(text, new Point(textBounds.X, CenteredTextY(textBounds, text)));
    }

    private static string ContactSummary(IReadOnlyList<ReplayContact> activeContacts)
    {
        var fingers = activeContacts.Count(contact => contact.Type == TouchType.Finger);
        var gloves = activeContacts.Count(contact => contact.Type == TouchType.Glove);
        var palms = activeContacts.Count(contact => contact.Type == TouchType.Palm);
        var other = activeContacts.Count - fingers - gloves - palms;
        var parts = new List<string>(4);
        AddContactCount(parts, fingers, "finger");
        AddContactCount(parts, gloves, "glove");
        AddContactCount(parts, palms, "palm");
        AddContactCount(parts, other, "other", pluralize: false);
        return parts.Count == 0 ? "0 contacts" : string.Join("  |  ", parts);
    }

    private static void AddContactCount(
        List<string> parts,
        int count,
        string label,
        bool pluralize = true)
    {
        if (count == 0) return;
        parts.Add($"{count} {label}{(pluralize && count != 1 ? "s" : string.Empty)}");
    }

    private Rect PositionLegend(Rect viewport, Size size)
    {
        const double inset = 12;
        var rightAligned = _overlay.LegendPosition is ReplayLegendPosition.TopRight or ReplayLegendPosition.BottomRight;
        var bottomAligned = _overlay.LegendPosition is ReplayLegendPosition.BottomLeft or ReplayLegendPosition.BottomRight;
        var x = rightAligned ? viewport.Right - inset - size.Width : viewport.Left + inset;
        var y = bottomAligned ? viewport.Bottom - inset - size.Height : viewport.Top + inset;
        return new Rect(x, y, size.Width, size.Height);
    }

    private static void DrawLegendType(
        DrawingContext context,
        Rect bounds,
        TouchType type,
        string label,
        ReplayLegendBrushes brushes)
    {
        ReplayPaintSurface.DrawTypeIcon(context, type, new Point(bounds.X + 4, bounds.Center.Y), brushes.AxisLabel, 3.5);
        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Cascadia Mono", FontStyle.Normal, FontWeight.Medium),
            12.5,
            brushes.AxisLabel);
        context.DrawText(text, new Point(bounds.X + 11, CenteredTextY(bounds, text)));
    }

    private static double CenteredTextY(Rect bounds, FormattedText text) =>
        Math.Round(bounds.Y + ((bounds.Height - text.Height) / 2), MidpointRounding.AwayFromZero);
}
