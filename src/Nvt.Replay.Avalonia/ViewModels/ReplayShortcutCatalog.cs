using Avalonia.Input;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Identifies the replay actions available through keyboard shortcuts.</summary>
public enum ReplayShortcutAction
{
    /// <summary>Requests loading or replacing the active capture.</summary>
    LoadCapture,
    /// <summary>Requests saving the replay review.</summary>
    SaveReview,
    /// <summary>Requests exporting replay analysis.</summary>
    ExportAnalysis,
    /// <summary>Requests starting or pausing replay playback.</summary>
    TogglePlayback,
    /// <summary>Requests navigation to the previous replay frame.</summary>
    PreviousFrame,
    /// <summary>Requests navigation to the next replay frame.</summary>
    NextFrame,
    /// <summary>Requests moving backward by ten replay frames.</summary>
    JumpBackTenFrames,
    /// <summary>Requests moving forward by ten replay frames.</summary>
    JumpForwardTenFrames,
    /// <summary>Requests navigation to the first replay frame.</summary>
    FirstFrame,
    /// <summary>Requests navigation to the last replay frame.</summary>
    LastFrame,
    /// <summary>Requests a decrease in playback speed.</summary>
    Slower,
    /// <summary>Requests an increase in playback speed.</summary>
    Faster,
    /// <summary>Requests playback at normal speed (1×).</summary>
    NormalSpeed,
    /// <summary>Requests setting the playback loop start.</summary>
    SetLoopIn,
    /// <summary>Requests setting the playback loop end.</summary>
    SetLoopOut,
    /// <summary>Requests marking the current replay frame.</summary>
    AddMarker,
    /// <summary>Requests showing or hiding the replay legend.</summary>
    ToggleLegend,
}

/// <summary>Stores a replay shortcut's action, display text, key binding, and editing availability.</summary>
/// <param name="Action">The replay action associated with this shortcut.</param>
/// <param name="Label">The display text describing the shortcut action.</param>
/// <param name="Gesture">The display text describing the key combination.</param>
/// <param name="Key">The primary key used to match the shortcut.</param>
/// <param name="Modifiers">The modifier keys required for a match.</param>
/// <param name="AlternateKey">An optional second key accepted with the same modifiers.</param>
/// <param name="AllowWhileEditing">Whether the shortcut is marked as allowed while editing.</param>
public sealed record ReplayShortcutDefinition(
    ReplayShortcutAction Action,
    string Label,
    string Gesture,
    Key Key,
    KeyModifiers Modifiers = KeyModifiers.None,
    Key? AlternateKey = null,
    bool AllowWhileEditing = false)
{
    /// <summary>Checks whether the key matches either binding and the modifiers match exactly.</summary>
    public bool Matches(Key key, KeyModifiers modifiers) =>
        (key == Key || key == AlternateKey) && modifiers == Modifiers;
}

/// <summary>Groups replay shortcut definitions under a title and description.</summary>
/// <param name="Title">The display title of the shortcut group.</param>
/// <param name="Description">The display text describing the group's purpose.</param>
/// <param name="Items">The shortcut definitions belonging to the group.</param>
public sealed record ReplayShortcutModule(
    string Title,
    string Description,
    IReadOnlyList<ReplayShortcutDefinition> Items);

/// <summary>Provides grouped replay shortcuts and lookup helpers for actions, keys, and tooltips.</summary>
public static class ReplayShortcutCatalog
{
    /// <summary>Gets the replay shortcut groups for playback, frame navigation, loop review, and file operations.</summary>
    public static IReadOnlyList<ReplayShortcutModule> Modules { get; } =
    [
        new("Playback", "Run and review at the selected clock rate",
        [
            new(ReplayShortcutAction.TogglePlayback, "Play / pause", "Space", Key.Space),
            new(ReplayShortcutAction.Slower, "Decrease speed", "[", Key.OemOpenBrackets),
            new(ReplayShortcutAction.Faster, "Increase speed", "]", Key.OemCloseBrackets),
            new(ReplayShortcutAction.NormalSpeed, "Return to 1×", "1", Key.D1, AlternateKey: Key.NumPad1),
        ]),
        new("Frame navigation", "Move precisely without changing source data",
        [
            new(ReplayShortcutAction.PreviousFrame, "Previous frame", "←", Key.Left),
            new(ReplayShortcutAction.NextFrame, "Next frame", "→", Key.Right),
            new(ReplayShortcutAction.JumpBackTenFrames, "Back 10 frames", "Shift + ←", Key.Left, KeyModifiers.Shift),
            new(ReplayShortcutAction.JumpForwardTenFrames, "Forward 10 frames", "Shift + →", Key.Right, KeyModifiers.Shift),
            new(ReplayShortcutAction.FirstFrame, "First frame", "Home", Key.Home),
            new(ReplayShortcutAction.LastFrame, "Last frame", "End", Key.End),
        ]),
        new("Loop & review", "Capture the moment that needs investigation",
        [
            new(ReplayShortcutAction.SetLoopIn, "Set loop start", "I", Key.I),
            new(ReplayShortcutAction.SetLoopOut, "Set loop end", "O", Key.O),
            new(ReplayShortcutAction.AddMarker, "Mark current frame", "M", Key.M),
            new(ReplayShortcutAction.ToggleLegend, "Show / hide legend", "L", Key.L),
        ]),
        new("File & report", "Keep capture and review operations available globally",
        [
            new(ReplayShortcutAction.LoadCapture, "Load / replace capture", "Ctrl + O", Key.O, KeyModifiers.Control, AllowWhileEditing: true),
            new(ReplayShortcutAction.SaveReview, "Save review", "Ctrl + S", Key.S, KeyModifiers.Control, AllowWhileEditing: true),
            new(ReplayShortcutAction.ExportAnalysis, "Export analysis", "Ctrl + E", Key.E, KeyModifiers.Control, AllowWhileEditing: true),
        ]),
    ];

    /// <summary>Gets every replay shortcut in module order.</summary>
    public static IEnumerable<ReplayShortcutDefinition> All => Modules.SelectMany(module => module.Items);

    /// <summary>Finds the first shortcut for a replay action, or null when none exists.</summary>
    public static ReplayShortcutDefinition? Find(ReplayShortcutAction action) =>
        All.FirstOrDefault(shortcut => shortcut.Action == action);

    /// <summary>Finds the first shortcut with a matching key and exact modifiers, or null when none exists.</summary>
    public static ReplayShortcutDefinition? Match(Key key, KeyModifiers modifiers) =>
        All.FirstOrDefault(shortcut => shortcut.Matches(key, modifiers));

    /// <summary>Formats a shortcut's label and gesture for a tooltip, or returns an empty string if no shortcut exists.</summary>
    public static string ToolTip(ReplayShortcutAction action)
    {
        var shortcut = Find(action);
        return shortcut is null ? string.Empty : $"{shortcut.Label} · {shortcut.Gesture}";
    }
}
