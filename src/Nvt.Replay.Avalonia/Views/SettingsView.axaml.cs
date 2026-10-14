using Avalonia.Controls;
using Avalonia.Interactivity;
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Avalonia.Views;

/// <summary>Stores the settings view's playback pause preferences.</summary>
/// <param name="PauseOnAlarmOrQaFail">Whether to pause playback on an alarm or QA failure.</param>
/// <param name="PauseOnBreak">Whether to pause playback on a reported contact Break.</param>
/// <param name="PauseOnAllBreak">Whether to pause playback on a decoded All Break.</param>
public sealed record ReplayPlaybackPreferences(
    bool PauseOnAlarmOrQaFail,
    bool PauseOnBreak,
    bool PauseOnAllBreak);

/// <summary>Displays application settings and raises requests for preference changes.</summary>
public partial class SettingsView : UserControl
{
    private bool synchronizingPlaybackPreferences;

    /// <summary>Initializes the settings controls and displays the available shortcut modules.</summary>
    public SettingsView()
    {
        InitializeComponent();
        SettingsShortcutModulesItemsControl.ItemsSource = ReplayShortcutCatalog.Modules;
    }

    /// <summary>Occurs when the user requests closing the settings view.</summary>
    public event EventHandler? CloseRequested;
    /// <summary>Occurs when the user requests switching the application theme.</summary>
    public event EventHandler? ThemeToggleRequested;
    /// <summary>Occurs when a playback pause option changes outside preference synchronization.</summary>
    public event EventHandler<ReplayPlaybackPreferences>? PlaybackPreferencesChanged;

    /// <summary>Gets the playback pause preferences selected by the settings checkboxes.</summary>
    public ReplayPlaybackPreferences PlaybackPreferences => new(
        PauseOnAlarmCheckBox.IsChecked == true,
        PauseOnBreakCheckBox.IsChecked == true,
        PauseOnAllBreakCheckBox.IsChecked == true);

    /// <summary>Updates the playback pause checkboxes without raising the preference change event.</summary>
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

    /// <summary>Selects the playback navigation button and brings the playback section into view.</summary>
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
