namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Represents a selectable UI option with a label, description, and stored value.</summary>
/// <param name="Label">The label displayed for the option.</param>
/// <param name="Description">The explanatory text displayed for the option.</param>
/// <param name="Value">The value used to identify the selected option.</param>
public sealed record SelectOption(string Label, string Description, string Value);
