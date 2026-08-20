using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.Views;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private bool synchronizingAutoPauseControls;
    private bool comboBoxDismissHandlersAttached;
    private void AttachComboBoxDismissHandlers()
    {
        if (comboBoxDismissHandlersAttached) return;
        comboBoxDismissHandlersAttached = true;
        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
            comboBox.DropDownClosed += ComboBox_OnAnyDropDownClosed;
    }

    private void ComboBox_OnAnyDropDownClosed(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);

    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (SettingsPage.IsVisible) CloseSettingsPage();
        else OpenSettingsPage();
    }

    private void OpenSettingsPage()
    {
        StopPlayback();
        StopOutputVideoPreviewPlayback();
        CloseCommandPalette();
        SetSourceTransportEnabled(false);
        SetOutputWorkspaceChrome(outputWorkspaceActive, showReplayTransport: false);
        SettingsPage.IsVisible = true;
        SettingsPage.Focus();
    }

    private void CloseSettingsPage()
    {
        SettingsPage.IsVisible = false;
        var paintSelected = ReferenceEquals(WorkspaceTabs.SelectedItem, PaintTab);
        SetOutputWorkspaceChrome(outputWorkspaceActive, paintSelected);
        SetSourceTransportEnabled(paintSelected && !outputWorkspaceActive);
        Focus();
    }

    private void SettingsPage_OnPlaybackPreferencesChanged(object? sender, ReplayPlaybackPreferences preferences)
    {
        ApplyPlaybackPreferences(preferences, syncTransportControls: true);
    }

    private void AutoPauseSettingsButton_OnClick(object? sender, RoutedEventArgs e) =>
        TransportAutoPausePanel.IsVisible = !TransportAutoPausePanel.IsVisible;

    private void OpenPlaybackSettingsButton_OnClick(object? sender, RoutedEventArgs e)
    {
        TransportAutoPausePanel.IsVisible = false;
        OpenSettingsPage();
        SettingsPage.NavigateToPlayback();
    }

    private void TransportAutoPauseOption_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (synchronizingAutoPauseControls || TransportPauseOnAlarmCheckBox is null) return;
        var preferences = new ReplayPlaybackPreferences(
            TransportPauseOnAlarmCheckBox.IsChecked == true,
            TransportPauseOnBreakCheckBox.IsChecked == true,
            TransportPauseOnAllBreakCheckBox.IsChecked == true);
        SettingsPage.SetPlaybackPreferences(preferences);
        ApplyPlaybackPreferences(preferences, syncTransportControls: false);
    }

    private void ApplyPlaybackPreferences(ReplayPlaybackPreferences preferences, bool syncTransportControls)
    {
        if (syncTransportControls)
        {
            synchronizingAutoPauseControls = true;
            try
            {
                TransportPauseOnAlarmCheckBox.IsChecked = preferences.PauseOnAlarmOrQaFail;
                TransportPauseOnBreakCheckBox.IsChecked = preferences.PauseOnBreak;
                TransportPauseOnAllBreakCheckBox.IsChecked = preferences.PauseOnAllBreak;
            }
            finally
            {
                synchronizingAutoPauseControls = false;
            }
        }
        playbackController.ConfigureAutoPause(new ReplayPlaybackPauseOptions(
            preferences.PauseOnBreak,
            preferences.PauseOnAllBreak));
        UpdateAutoPauseIndicator(preferences);
        if (reviewWorkspace is not null)
        {
            var enabled = preferences.PauseOnAlarmOrQaFail;
            reviewWorkspace.SetReviewOptions(
                reviewWorkspace.ReviewOptions with { PauseOnAlarm = enabled, PauseOnQaFail = enabled });
        }
    }

}
