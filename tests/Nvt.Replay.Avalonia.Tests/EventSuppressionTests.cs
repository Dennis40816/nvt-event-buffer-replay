using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class EventSuppressionTests
{
    [Fact]
    public void Nested_scopes_remain_active_until_the_outermost_exit()
    {
        var suppression = new EventSuppressionScope();
        Assert.False(suppression.IsActive);

        using (suppression.Enter())
        {
            Assert.True(suppression.IsActive);
            using (suppression.Enter())
            {
                Assert.True(suppression.IsActive);
            }
            Assert.True(suppression.IsActive);
        }

        Assert.False(suppression.IsActive);
    }

    [Fact]
    public void Exception_restores_suppression_and_allows_a_later_scope()
    {
        var suppression = new EventSuppressionScope();
        var failure = new InvalidOperationException("Injected control update failure.");

        var actual = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (suppression.Enter())
            {
                Assert.True(suppression.IsActive);
                throw failure;
            }
        }));

        Assert.Same(failure, actual);
        Assert.False(suppression.IsActive);
        using (suppression.Enter())
        {
            Assert.True(suppression.IsActive);
        }
        Assert.False(suppression.IsActive);
    }

    [AvaloniaFact]
    public async Task Register_profile_selection_handler_runs_after_a_suppressed_update_throws()
    {
        var window = new MainWindow { Width = 1180, Height = 720 };
        window.Show();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            var profile = Required<ComboBox>(window, "RegisterProfileComboBox");
            Assert.Equal("51927", Assert.IsType<RegisterProfileChoice>(profile.SelectedItem).IcFamily);

            var failure = new InvalidOperationException("Injected register profile selection failure.");
            EventHandler<SelectionChangedEventArgs> failSelection = (_, _) => throw failure;
            profile.SelectionChanged += failSelection;
            try
            {
                var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    window.ApplyStartupDecodeAsync("0x83", palmProfile: null, registerProfile: "51929/51932"));
                Assert.Same(failure, actual);
            }
            finally
            {
                profile.SelectionChanged -= failSelection;
            }

            // A normal control selection uses the XAML-wired handler, without entering suppression.
            profile.SelectedItem = profile.Items.OfType<RegisterProfileChoice>()
                .Single(choice => choice.IcFamily is null);
            var status = Required<TextBlock>(window, "SessionStatusText");
            await WaitUntilAsync(() => status.Text == "Register profile removed; original capture remains unchanged");
            Assert.Contains("IC profile unconfirmed", Required<TextBlock>(window, "ConfigurationHintText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control '{name}'.");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(predicate(), "Timed out while waiting for the register profile handler.");
    }
}
