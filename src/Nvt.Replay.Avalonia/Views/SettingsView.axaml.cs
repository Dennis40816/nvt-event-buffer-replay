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
    private bool synchronizingPlaybackPreferences;

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
        synchronizingPlaybackPreferences = true;
        try
        {
            PauseOnAlarmCheckBox.IsChecked = preferences.PauseOnAlarmOrQaFail;
            PauseOnBreakCheckBox.IsChecked = preferences.PauseOnBreak;
            PauseOnAllBreakCheckBox.IsChecked = preferences.PauseOnAllBreak;
        }
        finally
        {
            synchronizingPlaybackPreferences = false;
        }
    }

    public void NavigateToPlayback() => NavigateTo(PlaybackNavButton, PlaybackSection);

    private void CloseSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ThemeButton_OnClick(object? sender, RoutedEventArgs e) =>
        ThemeToggleRequested?.Invoke(this, EventArgs.Empty);

    private void PlaybackOption_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (!synchronizingPlaybackPreferences)
            PlaybackPreferencesChanged?.Invoke(this, PlaybackPreferences);
    }

    private void GeneralNavButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(GeneralNavButton, GeneralSection);

    private void PlaybackNavButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(PlaybackNavButton, PlaybackSection);

    private void OutputDefaultsNavButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(OutputDefaultsNavButton, OutputDefaultsSection);

    private void ShortcutsNavButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(ShortcutsNavButton, ShortcutsSection);

    private void PerformanceNavButton_OnClick(object? sender, RoutedEventArgs e) =>
        NavigateTo(PerformanceNavButton, PerformanceSection);

    private void NavigateTo(Button selected, Control section)
    {
        foreach (var button in new[]
                 {
                     GeneralNavButton,
                     PlaybackNavButton,
                     OutputDefaultsNavButton,
                     ShortcutsNavButton,
                     PerformanceNavButton,
                 })
            button.Classes.Set("active", ReferenceEquals(button, selected));
        section.BringIntoView();
    }
}
