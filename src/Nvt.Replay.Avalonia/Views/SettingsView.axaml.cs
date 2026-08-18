using Avalonia.Controls;
using Avalonia.Interactivity;
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Avalonia.Views;

public sealed record ReplayPlaybackPreferences(
    bool PauseOnAlarmOrQaFail,
    bool PauseOnBreak,
    bool PauseOnAllBreak);

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        SettingsShortcutModulesItemsControl.ItemsSource = ReplayShortcutCatalog.Modules;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler? ThemeToggleRequested;
    public event EventHandler<ReplayPlaybackPreferences>? PlaybackPreferencesChanged;

    public ReplayPlaybackPreferences PlaybackPreferences => new(
        PauseOnAlarmCheckBox.IsChecked == true,
        PauseOnBreakCheckBox.IsChecked == true,
        PauseOnAllBreakCheckBox.IsChecked == true);

    public void SetPlaybackPreferences(ReplayPlaybackPreferences preferences)
    {
        PauseOnAlarmCheckBox.IsChecked = preferences.PauseOnAlarmOrQaFail;
        PauseOnBreakCheckBox.IsChecked = preferences.PauseOnBreak;
        PauseOnAllBreakCheckBox.IsChecked = preferences.PauseOnAllBreak;
    }

    private void CloseSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e) =>
        ThemeToggleRequested?.Invoke(this, EventArgs.Empty);

    private void PlaybackOption_OnChanged(object? sender, RoutedEventArgs e) =>
        PlaybackPreferencesChanged?.Invoke(this, PlaybackPreferences);
}
