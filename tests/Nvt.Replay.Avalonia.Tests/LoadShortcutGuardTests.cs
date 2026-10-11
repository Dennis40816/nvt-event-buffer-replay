using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Nvt.Replay.Avalonia.Views;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class LoadShortcutGuardTests
{
    [AvaloniaFact]
    public async Task The_load_shortcut_is_not_handled_while_a_load_is_in_progress()
    {
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        window.Show();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            var load = window.OpenCaptureAsync(fixture);
            var cancelButton = window.FindControl<Button>("CancelButton");
            Assert.True(cancelButton?.IsVisible, "The load must be busy before the key press.");

            var keyDown = new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.O,
                KeyModifiers = KeyModifiers.Control,
            };
            window.RaiseEvent(keyDown);

            Assert.False(keyDown.Handled);
            await load;
        }
        finally
        {
            window.Close();
        }
    }
}