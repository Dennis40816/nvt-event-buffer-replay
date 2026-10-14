namespace Nvt.Replay.Rendering;

/// <summary>Selects the dark or light color palette used to render replay frames.</summary>
public enum ReplayRenderTheme
{
    /// <summary>Uses the dark replay color palette.</summary>
    Dark,
    /// <summary>Uses the light replay color palette.</summary>
    Light,
}

/// <summary>Controls replay contact rendering, colors, grid emphasis, legend layout, frame chrome, and trails.</summary>
/// <param name="Mode">The selection of reported contacts, host-state contacts, or both to render.</param>
/// <param name="Theme">The color theme used to render replay frames.</param>
/// <param name="StrongGrid">Whether to emphasize grid lines and viewport borders.</param>
/// <param name="LegendVisible">Whether to display the contact legend.</param>
/// <param name="LegendCollapsed">Whether to show a contact summary instead of individual legend entries.</param>
/// <param name="LegendPosition">The position of the contact legend within the viewport.</param>
/// <param name="ShowFrameChrome">Whether to draw the frame header and footer.</param>
/// <param name="ShowTrailLines">Whether to draw lines connecting contact trail points.</param>
/// <param name="ShowTrailPoints">Whether to draw markers at contact trail points.</param>
public sealed record ReplayRenderSettings(
    ReplayRenderMode Mode = ReplayRenderMode.Compare,
    ReplayRenderTheme Theme = ReplayRenderTheme.Dark,
    bool StrongGrid = false,
    bool LegendVisible = true,
    bool LegendCollapsed = true,
    ReplayLegendPosition LegendPosition = ReplayLegendPosition.TopLeft,
    bool ShowFrameChrome = false,
    bool ShowTrailLines = true,
    bool ShowTrailPoints = true);

/// <summary>Stores the red, green, and blue byte components of a replay drawing color.</summary>
/// <param name="Red">The red color component.</param>
/// <param name="Green">The green color component.</param>
/// <param name="Blue">The blue color component.</param>
public readonly record struct ReplayColor(byte Red, byte Green, byte Blue)
{
    /// <summary>Parses a six-digit hexadecimal RGB color after removing leading hash characters.</summary>
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

    /// <summary>Gets the RGB color as a hash-prefixed six-digit uppercase hexadecimal string.</summary>
    public string Hex => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

/// <summary>Holds colors for replay backgrounds, grid lines, labels, contact states, and contact IDs.</summary>
/// <param name="Stage">The background color of the replay drawing stage.</param>
/// <param name="Surface">The background color of the contact coordinate viewport.</param>
/// <param name="Grid">The color of grid lines when grid emphasis is disabled.</param>
/// <param name="StrongGrid">The color of grid lines when grid emphasis is enabled.</param>
/// <param name="Axis">The normal viewport border color, also used for frame separators and legend borders.</param>
/// <param name="StrongAxis">The color of emphasized viewport borders and axis tick marks.</param>
/// <param name="AxisLabel">The color of axis labels and secondary annotation text.</param>
/// <param name="LabelSurface">The background color of contact labels and legends.</param>
/// <param name="HoverLabelSurface">The background color of hover labels.</param>
/// <param name="LabelText">The color of primary label, legend, and frame header text.</param>
/// <param name="Break">The color of contact markers with break status.</param>
/// <param name="Palm">The color of palm contact markers.</param>
/// <param name="Alarm">The color of alarm indicators and the global palm viewport border.</param>
/// <param name="Invalid">The color of invalid contact markers.</param>
/// <param name="ContactColors">The ordered colors assigned cyclically to contact IDs.</param>
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

/// <summary>Provides replay theme palettes and contact ID color selection.</summary>
public static class ReplayVisualStyle
{
    /// <summary>Gets the palette used for rendering replay frames with the dark theme.</summary>
    public static ReplayVisualPalette Dark { get; } = Palette(
        "#10171A", "#162428", "#26383D", "#34464F", "#324149", "#60747D", "#829097",
        "#101619", "#182226", "#F3F7F5", "#F1A85B", "#D98AC6", "#E86A64", "#F06B6B",
        "#67D5E4", "#C8F36C", "#FFBE6B", "#BBA2FF", "#FF7F91",
        "#78A9FF", "#66D39A", "#E48CE0", "#F2DD70", "#76D7B7");

    /// <summary>Gets the palette used for rendering replay frames with the light theme.</summary>
    public static ReplayVisualPalette Light { get; } = Palette(
        "#EDF2EF", "#FBFDFC", "#DCE5E1", "#C8D5D0", "#B5C6BF", "#819890", "#586D64",
        "#FFFFFF", "#FFFFFF", "#182821", "#A65D10", "#8F467D", "#B63D3A", "#C13D3D",
        "#007C91", "#5D8100", "#B56500", "#6848C7", "#C53A59",
        "#3567C8", "#27855A", "#A14299", "#8E7600", "#177D68");

    /// <summary>Returns the light palette for the light theme and the dark palette for every other value.</summary>
    public static ReplayVisualPalette For(ReplayRenderTheme theme) =>
        theme == ReplayRenderTheme.Light ? Light : Dark;

    /// <summary>Selects a cyclic palette color by contact ID, treating zero as ID one.</summary>
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
