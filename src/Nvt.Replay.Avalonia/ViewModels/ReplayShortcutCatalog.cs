using Avalonia.Input;

namespace Nvt.Replay.Avalonia.ViewModels;

public enum ReplayShortcutAction
{
    LoadCapture,
    SaveReview,
    ExportAnalysis,
    TogglePlayback,
    PreviousFrame,
    NextFrame,
    JumpBackTenFrames,
    JumpForwardTenFrames,
    FirstFrame,
    LastFrame,
    Slower,
    Faster,
    NormalSpeed,
    SetLoopIn,
    SetLoopOut,
    AddMarker,
    ToggleLegend,
}

public sealed record ReplayShortcutDefinition(
    ReplayShortcutAction Action,
    string Label,
    string Gesture,
    Key Key,
    KeyModifiers Modifiers = KeyModifiers.None,
    Key? AlternateKey = null,
    bool AllowWhileEditing = false)
{
    public bool Matches(Key key, KeyModifiers modifiers) =>
        (key == Key || key == AlternateKey) && modifiers == Modifiers;
}

public sealed record ReplayShortcutModule(
    string Title,
    string Description,
    IReadOnlyList<ReplayShortcutDefinition> Items);

public static class ReplayShortcutCatalog
{
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

    public static IEnumerable<ReplayShortcutDefinition> All => Modules.SelectMany(module => module.Items);

    public static ReplayShortcutDefinition? Find(ReplayShortcutAction action) =>
        All.FirstOrDefault(shortcut => shortcut.Action == action);

    public static ReplayShortcutDefinition? Match(Key key, KeyModifiers modifiers) =>
        All.FirstOrDefault(shortcut => shortcut.Matches(key, modifiers));

    public static string ToolTip(ReplayShortcutAction action)
    {
        var shortcut = Find(action);
        return shortcut is null ? string.Empty : $"{shortcut.Label} · {shortcut.Gesture}";
    }
}
