namespace Nvt.Replay.Rendering;

public enum ReplayRenderTheme
{
    Dark,
    Light,
}

public sealed record ReplayRenderSettings(
    ReplayRenderMode Mode = ReplayRenderMode.Compare,
    ReplayRenderTheme Theme = ReplayRenderTheme.Dark,
    bool StrongGrid = false,
    bool LegendVisible = true,
    bool LegendCollapsed = true,
    ReplayLegendPosition LegendPosition = ReplayLegendPosition.TopLeft);

public readonly record struct ReplayColor(byte Red, byte Green, byte Blue)
{
    public static ReplayColor Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var hex = value.TrimStart('#');
        if (hex.Length != 6) throw new FormatException($"Replay colors must use six hexadecimal digits: {value}");
        return new ReplayColor(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16));
    }

    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

public sealed record ReplayVisualPalette(
    ReplayColor Stage,
    ReplayColor Surface,
    ReplayColor Grid,
    ReplayColor StrongGrid,
    ReplayColor Axis,
    ReplayColor StrongAxis,
    ReplayColor AxisLabel,
    ReplayColor LabelSurface,
    ReplayColor HoverLabelSurface,
    ReplayColor LabelText,
    ReplayColor Break,
    ReplayColor Palm,
    ReplayColor Alarm,
    ReplayColor Invalid,
    IReadOnlyList<ReplayColor> ContactColors);

public static class ReplayVisualStyle
{
    public static ReplayVisualPalette Dark { get; } = Palette(
        "#10171A", "#162428", "#26383D", "#34464F", "#324149", "#60747D", "#829097",
        "#101619", "#182226", "#F3F7F5", "#F1A85B", "#D98AC6", "#E86A64", "#F06B6B",
        "#67D5E4", "#C8F36C", "#FFBE6B", "#BBA2FF", "#FF7F91",
        "#78A9FF", "#66D39A", "#E48CE0", "#F2DD70", "#76D7B7");

    public static ReplayVisualPalette Light { get; } = Palette(
        "#EDF2EF", "#FBFDFC", "#DCE5E1", "#C8D5D0", "#B5C6BF", "#819890", "#586D64",
        "#FFFFFF", "#FFFFFF", "#182821", "#A65D10", "#8F467D", "#B63D3A", "#C13D3D",
        "#007C91", "#5D8100", "#B56500", "#6848C7", "#C53A59",
        "#3567C8", "#27855A", "#A14299", "#8E7600", "#177D68");

    public static ReplayVisualPalette For(ReplayRenderTheme theme) =>
        theme == ReplayRenderTheme.Light ? Light : Dark;

    public static ReplayColor ContactColor(ReplayVisualPalette palette, byte id) =>
        palette.ContactColors[(Math.Max(1, (int)id) - 1) % palette.ContactColors.Count];

    private static ReplayVisualPalette Palette(
        string stage,
        string surface,
        string grid,
        string strongGrid,
        string axis,
        string strongAxis,
        string axisLabel,
        string labelSurface,
        string hoverLabelSurface,
        string labelText,
        string @break,
        string palm,
        string alarm,
        string invalid,
        params string[] contactColors) =>
        new(
            ReplayColor.Parse(stage),
            ReplayColor.Parse(surface),
            ReplayColor.Parse(grid),
            ReplayColor.Parse(strongGrid),
            ReplayColor.Parse(axis),
            ReplayColor.Parse(strongAxis),
            ReplayColor.Parse(axisLabel),
            ReplayColor.Parse(labelSurface),
            ReplayColor.Parse(hoverLabelSurface),
            ReplayColor.Parse(labelText),
            ReplayColor.Parse(@break),
            ReplayColor.Parse(palm),
            ReplayColor.Parse(alarm),
            ReplayColor.Parse(invalid),
            contactColors.Select(ReplayColor.Parse).ToArray());
}
