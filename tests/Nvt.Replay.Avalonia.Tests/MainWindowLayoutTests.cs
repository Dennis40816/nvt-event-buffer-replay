using System.Security.Cryptography;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Formats.Common;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowLayoutTests
{
    [AvaloniaFact]
    public async Task Confirming_a_common_version_decodes_immediately_without_an_action_button()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");

            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var frames = Required<ListBox>(window, "DecodedFramesList");
            var hint = Required<TextBlock>(window, "ConfigurationHintText");
            var version = Required<ComboBox>(window, "EventVersionComboBox");
            Assert.Equal(1, version.SelectedIndex);
            Assert.Equal(19, frames.ItemCount);
            Assert.Contains("decoded automatically", hint.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Null(window.FindControl<Button>("DecodeButton"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Decode_configuration_is_automatic_compact_and_vertically_centered()
    {
        var window = ShowWindow();
        try
        {
            var version = Required<ComboBox>(window, "EventVersionComboBox");
            var profile = Required<ComboBox>(window, "RegisterProfileComboBox");
            var panelWidth = Required<TextBox>(window, "PanelWidthTextBox");
            var load = Required<Button>(window, "LoadButton");
            var save = Required<Button>(window, "SaveReviewButton");

            Assert.Null(window.FindControl<Button>("DecodeButton"));
            Assert.Equal("Select", version.PlaceholderText);
            Assert.Equal(34, version.Bounds.Height);
            Assert.Equal(34, profile.Bounds.Height);
            Assert.Equal(34, panelWidth.Height);
            Assert.Equal(36, load.Bounds.Height);
            Assert.Equal(VerticalAlignment.Center, version.VerticalAlignment);
            Assert.Equal(VerticalAlignment.Center, panelWidth.VerticalAlignment);
            Assert.Equal(VerticalAlignment.Center, load.VerticalContentAlignment);
            Assert.Equal(
                global::Avalonia.Media.Colors.Transparent,
                Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(save.Background).Color);
            Assert.Equal(
                global::Avalonia.Media.Colors.Transparent,
                Assert.IsAssignableFrom<global::Avalonia.Media.ISolidColorBrush>(save.BorderBrush).Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Empty_review_queue_starts_collapsed_and_keeps_the_canvas_wide()
    {
        var window = ShowWindow();
        try
        {
            var shell = Required<Grid>(window, "WorkspaceShellGrid");
            var reviewContent = Required<Grid>(window, "ReviewRailContent");
            var inspectorContent = Required<Grid>(window, "InspectorRailContent");

            Assert.Equal(new GridLength(42), shell.ColumnDefinitions[0].Width);
            Assert.False(reviewContent.IsVisible);
            Assert.Equal(new GridLength(4), shell.ColumnDefinitions[2].Width);
            Assert.Equal(new GridLength(380), shell.ColumnDefinitions[3].Width);
            Assert.True(inspectorContent.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Main_workspace_renders_in_both_dark_and_light_themes()
    {
        var window = ShowWindow();
        try
        {
            var application = Assert.IsType<App>(Application.Current);

            application.RequestedThemeVariant = ThemeVariant.Dark;
            var dark = CaptureHash(window, "01-empty-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            var light = CaptureHash(window, "02-empty-light.png");

            Assert.NotEqual(dark, light);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Golden_capture_renders_distinct_Paint_and_exact_MP4_preview_themes()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            var application = Assert.IsType<App>(Application.Current);
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            Dispatcher.UIThread.RunJobs();
            Required<ListBox>(window, "InspectorContactsList").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();

            application.RequestedThemeVariant = ThemeVariant.Dark;
            var paintDark = CaptureHash(window, "03-golden-paint-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            var paintLight = CaptureHash(window, "04-golden-paint-light.png");
            Required<Button>(window, "InspectorRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var collapsedLight = CaptureHash(window, "07-golden-inspector-collapsed-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            var collapsedDark = CaptureHash(window, "08-golden-inspector-collapsed-dark.png");
            Required<Button>(window, "InspectorRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var tabs = Required<TabControl>(window, "WorkspaceTabs");
            tabs.SelectedItem = Required<TabItem>(window, "AnalysisTab");
            var previewFrame = Required<TextBlock>(window, "OutputPreviewFrameText");
            await WaitUntilAsync(() => !string.Equals(previewFrame.Text, "output 0/0", StringComparison.Ordinal));
            Assert.Contains("1280 × 720", Required<TextBlock>(window, "OutputVideoBadgeText").Text);
            Assert.False(Required<Button>(window, "PlayPauseButton").IsEnabled);
            Assert.True(Required<Button>(window, "OutputPreviewPlayPauseButton").IsEnabled);
            var sourceLines = Required<TextBlock>(window, "OutputSourceText").Text!.Split('\n');
            Assert.Equal(32, sourceLines[^2].Length);
            Assert.Equal(32, sourceLines[^1].Length);
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var outputLight = CaptureHash(window, "05-golden-output-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            var outputDark = CaptureHash(window, "06-golden-output-dark.png");

            var outputContent = Required<ComboBox>(window, "OutputContentComboBox");
            outputContent.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var heatmapDark = CaptureHash(window, "09-golden-heatmap-dark.png");
            outputContent.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var packageDark = CaptureHash(window, "10-golden-package-dark.png");
            outputContent.SelectedIndex = 0;
            Required<ToggleButton>(window, "OutputSettingsToggleButton").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var settingsDark = CaptureHash(window, "11-golden-output-settings-dark.png");
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 5;
            Required<ComboBox>(window, "OutputResolutionComboBox").SelectedIndex = 3;
            Dispatcher.UIThread.RunJobs();
            var customSettingsDark = CaptureHash(window, "15-golden-output-custom-settings-dark.png");
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 2;
            Required<ComboBox>(window, "OutputResolutionComboBox").SelectedIndex = 0;
            Required<ToggleButton>(window, "OutputSettingsToggleButton").IsChecked = false;
            window.Width = 1180;
            window.Height = 720;
            Dispatcher.UIThread.RunJobs();
            var compactOutputDark = CaptureHash(window, "12-golden-output-compact-dark.png");
            outputContent.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var compactHeatmapDark = CaptureHash(window, "13-golden-heatmap-compact-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var compactHeatmapLight = CaptureHash(window, "14-golden-heatmap-compact-light.png");

            Assert.NotEqual(paintDark, paintLight);
            Assert.NotEqual(collapsedDark, collapsedLight);
            Assert.NotEqual(outputDark, outputLight);
            Assert.NotEqual(paintDark, outputDark);
            Assert.NotEqual(outputDark, heatmapDark);
            Assert.NotEqual(heatmapDark, packageDark);
            Assert.NotEqual(outputDark, settingsDark);
            Assert.NotEqual(settingsDark, customSettingsDark);
            Assert.NotEqual(outputDark, compactOutputDark);
            Assert.NotEqual(compactHeatmapDark, compactHeatmapLight);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_contacts_select_the_matching_canvas_contact_and_collapsed_summary_stays_useful()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            Dispatcher.UIThread.RunJobs();

            var contacts = Required<ListBox>(window, "InspectorContactsList");
            Assert.Equal(3, contacts.ItemCount);
            contacts.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var selected = Assert.IsType<InspectorContactRow>(contacts.SelectedItem);
            Assert.Equal(selected.Id, Required<ReplayPaintSurface>(window, "PaintSurface").HighlightedContactId);
            Assert.Equal("3 active", Required<TextBlock>(window, "InspectorContactCountText").Text);

            Required<Button>(window, "InspectorRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var shell = Required<Grid>(window, "WorkspaceShellGrid");
            Assert.Equal(new GridLength(58), shell.ColumnDefinitions[3].Width);
            Assert.True(Required<Grid>(window, "InspectorCollapsedSummary").IsVisible);
            Assert.False(Required<Grid>(window, "InspectorRailContent").IsVisible);
            Assert.Equal("3", Required<TextBlock>(window, "CollapsedFingerText").Text);
            Assert.Equal("ASIL\nNORM", Required<TextBlock>(window, "CollapsedAsilText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_header_layers_copy_and_all_break_visibility_match_the_selected_golden_frame()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("3", Required<TextBlock>(window, "InspectorFingerText").Text);
            Assert.Equal("0", Required<TextBlock>(window, "InspectorGloveText").Text);
            Assert.Equal("0", Required<TextBlock>(window, "InspectorPalmText").Text);
            Assert.Equal("OK", Required<TextBlock>(window, "InspectorCrcText").Text);
            Assert.Equal("NORMAL", Required<TextBlock>(window, "InspectorAsilText").Text);
            Assert.Equal("0x00", Required<TextBlock>(window, "InspectorAsilRawText").Text);
            Assert.False(Required<Border>(window, "InspectorAllBreakBadge").IsVisible);
            Assert.NotEqual("-", Required<TextBlock>(window, "InspectorTimestampText").Text);

            var details = Required<TabControl>(window, "InspectorDetailsTabs");
            Assert.Equal(["Protocol", "Raw", "Review"], details.Items.OfType<TabItem>().Select(tab => tab.Header?.ToString()));
            Assert.True(Required<ItemsControl>(window, "ProtocolFieldsItemsControl").ItemCount > 0);
            var sourceIdentity = Required<Expander>(window, "SourceIdentityExpander");
            Assert.False(sourceIdentity.IsExpanded);
            details.SelectedItem = Required<TabItem>(window, "InspectorRawTab");
            sourceIdentity.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<ItemsControl>(window, "RawByteSectionsItemsControl").ItemCount > 0);
            Assert.NotEqual("-", Required<TextBlock>(window, "StableIdText").Text);
            sourceIdentity.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "20-inspector-source-identity-dark.png");

            Required<Button>(window, "CopyInspectorButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => Required<TextBlock>(window, "SessionStatusText").Text?.StartsWith("Copied frame", StringComparison.Ordinal) == true);
            var clipboard = TopLevel.GetTopLevel(window)?.Clipboard ?? throw new InvalidOperationException("Headless clipboard is unavailable.");
            var copied = await clipboard.TryGetTextAsync();
            Assert.Contains("contacts 3", copied);
            Assert.Contains("CRC OK  ASIL NORMAL 0x00", copied);

            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().Last();
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Border>(window, "InspectorAllBreakBadge").IsVisible);
            Assert.Equal("ALL BREAK", Required<TextBlock>(window, "InspectorAllBreakText").Text);
            Assert.Equal("0 active", Required<TextBlock>(window, "InspectorContactCountText").Text);
            Assert.False(Required<ListBox>(window, "InspectorContactsList").IsVisible);

            var application = Assert.IsType<App>(Application.Current);
            var inspectorColumn = Required<Grid>(window, "WorkspaceShellGrid").ColumnDefinitions[3];
            inspectorColumn.Width = new GridLength(320);
            Dispatcher.UIThread.RunJobs();
            var allBreakBounds = BoundsInside(
                Required<Border>(window, "InspectorAllBreakBadge"),
                Required<Grid>(window, "InspectorRailContent"));
            Assert.True(allBreakBounds.Left >= 0);
            Assert.True(allBreakBounds.Right <= Required<Grid>(window, "InspectorRailContent").Bounds.Width + 0.5);
            application.RequestedThemeVariant = ThemeVariant.Dark;
            var allBreakDark = CaptureHash(window, "16-golden-inspector-all-break-dark.png");
            inspectorColumn.Width = new GridLength(520);
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var allBreakLight = CaptureHash(window, "17-golden-inspector-all-break-light.png");
            Assert.NotEqual(allBreakDark, allBreakLight);
            application.RequestedThemeVariant = ThemeVariant.Dark;
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_alarm_crc_failure_and_finding_navigation_are_visible_and_actionable()
    {
        var capture = await WriteKingstVisCaptureAsync(
            CommonPacket(asil: 0x02, corruptCrc: true, x: 120),
            CommonPacket(asil: 0x02, corruptCrc: true, x: 240));
        var window = ShowWindow();
        try
        {
            await window.OpenCaptureAsync(capture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("FAIL", Required<TextBlock>(window, "InspectorCrcText").Text);
            Assert.Equal("ALARM", Required<TextBlock>(window, "InspectorAsilText").Text);
            Assert.Equal("0x02", Required<TextBlock>(window, "InspectorAsilRawText").Text);
            Assert.Contains("healthTextAlarm", Required<TextBlock>(window, "InspectorCrcText").Classes);
            Assert.Contains("healthTextAlarm", Required<TextBlock>(window, "InspectorAsilText").Classes);
            Assert.False(Required<Border>(window, "InspectorAllBreakBadge").IsVisible);
            Assert.True(Required<Border>(window, "InspectorAlertBorder").IsVisible);
            Assert.True(Required<Button>(window, "PreviousFindingButton").IsVisible);
            Assert.True(Required<Button>(window, "NextFindingButton").IsVisible);

            var application = Assert.IsType<App>(Application.Current);
            application.RequestedThemeVariant = ThemeVariant.Dark;
            var alarmDark = CaptureHash(window, "18-inspector-alarm-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var alarmLight = CaptureHash(window, "19-inspector-alarm-light.png");
            Assert.NotEqual(alarmDark, alarmLight);

            Required<Button>(window, "NextFindingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("2 / 2", Required<TextBlock>(window, "InspectorLogicalText").Text);
            Required<Button>(window, "PreviousFindingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("1 / 2", Required<TextBlock>(window, "InspectorLogicalText").Text);
            Required<TabControl>(window, "InspectorDetailsTabs").SelectedItem = Required<TabItem>(window, "InspectorReviewTab");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "21-inspector-review-selected-dark.png");
        }
        finally
        {
            window.Close();
            File.Delete(capture);
        }
    }

    [AvaloniaFact]
    public void Inspector_width_is_bounded_and_restored_after_collapsing()
    {
        var window = ShowWindow();
        try
        {
            var shell = Required<Grid>(window, "WorkspaceShellGrid");
            var inspectorColumn = shell.ColumnDefinitions[3];
            Assert.Equal(320, inspectorColumn.MinWidth);
            Assert.Equal(520, inspectorColumn.MaxWidth);
            Assert.True(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);

            inspectorColumn.Width = new GridLength(500);
            Dispatcher.UIThread.RunJobs();
            var toggle = Required<Button>(window, "InspectorRailToggleButton");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new GridLength(58), inspectorColumn.Width);
            Assert.False(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new GridLength(500), inspectorColumn.Width);
            Assert.True(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_scrolls_to_tenth_contact_and_preserves_mixed_type_counts_and_icons()
    {
        var capture = await WriteKingstVisCaptureAsync(MixedTenContactPacket());
        var window = ShowWindow();
        try
        {
            await window.OpenCaptureAsync(capture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var contacts = Required<ListBox>(window, "InspectorContactsList");
            Assert.Equal(10, contacts.ItemCount);
            Assert.Equal("8", Required<TextBlock>(window, "InspectorFingerText").Text);
            Assert.Equal("1", Required<TextBlock>(window, "InspectorGloveText").Text);
            Assert.Equal("1", Required<TextBlock>(window, "InspectorPalmText").Text);

            contacts.SelectedIndex = 9;
            contacts.ScrollIntoView(contacts.SelectedItem!);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((byte)10, Assert.IsType<InspectorContactRow>(contacts.SelectedItem).Id);
            Assert.Equal((byte)10, Required<ReplayPaintSurface>(window, "PaintSurface").HighlightedContactId);
            Assert.Contains(window.GetVisualDescendants().OfType<TouchTypeIcon>(), icon =>
                icon.ContactId == 9 && icon.TouchType == Nvt.Replay.Core.TouchType.Glove);
            Assert.Contains(window.GetVisualDescendants().OfType<TouchTypeIcon>(), icon =>
                icon.ContactId == 10 && icon.TouchType == Nvt.Replay.Core.TouchType.Palm);

            Required<Grid>(window, "WorkspaceShellGrid").ColumnDefinitions[3].Width = new GridLength(520);
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "22-inspector-ten-contacts-dark.png");
            Required<Button>(window, "InspectorRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("8", Required<TextBlock>(window, "CollapsedFingerText").Text);
            Assert.Equal("1", Required<TextBlock>(window, "CollapsedGloveText").Text);
            Assert.Equal("1", Required<TextBlock>(window, "CollapsedPalmText").Text);
        }
        finally
        {
            window.Close();
            File.Delete(capture);
        }
    }

    [AvaloniaFact]
    public void Drop_downs_reserve_the_longest_visible_option_without_fixed_widths()
    {
        var window = ShowWindow();
        try
        {
            var names = new[]
            {
                "SourceAdapterComboBox",
                "EventVersionComboBox",
                "RegisterProfileComboBox",
                "Desay97ProfileComboBox",
                "RegisterFilterComboBox",
                "PaintModeComboBox",
                "TrailModeComboBox",
                "TrailLengthComboBox",
                "LegendPositionComboBox",
                "ReviewOccurrenceComboBox",
                "ClockModeComboBox",
                "ReplaySpeedComboBox",
                "OutputContentComboBox",
                "OutputClockComboBox",
                "OutputSpeedComboBox",
                "OutputFrameRateComboBox",
                "OutputResolutionComboBox",
            };

            foreach (var name in names)
            {
                var comboBox = Required<ComboBox>(window, name);
                Assert.True(double.IsNaN(comboBox.Width), $"{name} still has a fixed width.");
                Assert.True(
                    comboBox.MinWidth >= ComboBoxAutoSizer.RequiredWidth(comboBox),
                    $"{name} does not reserve its longest option.");
            }

            var legend = Required<ComboBox>(window, "LegendPositionComboBox");
            var selectedWidth = ComboBoxAutoSizer.MeasureLabelWidth(legend, "Auto");
            var longestWidth = ComboBoxAutoSizer.MeasureLabelWidth(legend, "Bottom right");
            Assert.True(longestWidth > selectedWidth);
            Assert.True(legend.MinWidth >= longestWidth + ComboBoxAutoSizer.ChromeAndPaddingWidth);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Output_selector_shows_one_primary_surface_and_video_settings_drive_preview_sampling()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputPreviewFrameText").Text != "output 0/0");

            var outputContent = Required<ComboBox>(window, "OutputContentComboBox");
            Assert.Equal(3, outputContent.ItemCount);
            Assert.True(Required<Grid>(window, "OutputVideoPanel").IsVisible);
            Assert.False(Required<Border>(window, "OutputHeatmapPanel").IsVisible);
            Assert.False(Required<Border>(window, "OutputPackagePanel").IsVisible);

            outputContent.SelectedIndex = 1;
            Assert.False(Required<Grid>(window, "OutputVideoPanel").IsVisible);
            Assert.True(Required<Border>(window, "OutputHeatmapPanel").IsVisible);
            Assert.Equal("Export PNG", Required<Button>(window, "ExportSelectedOutputButton").Content);

            outputContent.SelectedIndex = 0;
            var settings = Required<ToggleButton>(window, "OutputSettingsToggleButton");
            settings.IsChecked = true;
            Assert.True(Required<Border>(window, "OutputVideoSettingsPanel").IsVisible);
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 5;
            var customSpeed = Required<TextBox>(window, "OutputCustomSpeedTextBox");
            Assert.True(customSpeed.IsVisible);
            customSpeed.Text = "1.75";
            customSpeed.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputVideoBadgeText").Text?.Contains("1.75×", StringComparison.Ordinal) == true);

            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 3;
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputVideoBadgeText").Text?.Contains("2×", StringComparison.Ordinal) == true);
            Assert.Contains("2×", Required<TextBlock>(window, "OutputVideoBadgeText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Timeline_empty_space_click_seeks_to_the_nearest_logical_frame()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var timeline = Required<Control>(window, "ReplayTimelineSurface");
            var localPoint = new Point(timeline.Bounds.Width * 0.75, timeline.Bounds.Height * 0.32);
            var windowPoint = timeline.TranslatePoint(localPoint, window) ??
                throw new InvalidOperationException("Timeline could not translate its click point to the window.");
            var hit = window.InputHitTest(windowPoint);
            Assert.Same(timeline, hit);

            window.MouseDown(windowPoint, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(windowPoint, MouseButton.Left, RawInputModifiers.None);

            Assert.Equal("15 / 19", Required<TextBlock>(window, "InspectorLogicalText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Timeline_toolbar_groups_actions_and_places_counts_beside_their_tracks_at_minimum_width()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            window.Width = 1180;
            Dispatcher.UIThread.RunJobs();

            var bar = Required<Grid>(window, "TimelineControlBar");
            var playback = Required<StackPanel>(window, "TransportPlaybackGroup");
            var timing = Required<Grid>(window, "TransportTimingGroup");
            var annotation = Required<StackPanel>(window, "TransportAnnotationGroup");
            var view = Required<StackPanel>(window, "TransportViewGroup");
            var groups = new Control[] { playback, timing, annotation, view };
            var bounds = groups.Select(control => BoundsInside(control, bar)).ToArray();

            Assert.All(bounds, rect =>
            {
                Assert.True(rect.Left >= 0);
                Assert.True(rect.Right <= bar.Bounds.Width + 0.5);
                Assert.InRange(rect.Center.Y, (bar.Bounds.Height / 2) - 1, (bar.Bounds.Height / 2) + 1);
            });
            Assert.True(bounds[0].Right < bounds[1].Left);
            Assert.True(bounds[1].Right < bounds[2].Left);
            Assert.True(bounds[2].Right < bounds[3].Left);

            Assert.Equal("38", Required<TextBlock>(window, "TimelinePhysicalCountText").Text);
            Assert.Equal("19", Required<TextBlock>(window, "TimelineLogicalCountText").Text);
            Assert.Equal("0", Required<TextBlock>(window, "TimelineEvidenceCountText").Text);
            Assert.Null(window.FindControl<TextBlock>("TimelineSummaryText"));
            Assert.False(Required<Button>(window, "ClearLoopButton").IsVisible);

            var application = Assert.IsType<App>(Application.Current);
            application.RequestedThemeVariant = ThemeVariant.Dark;
            var dark = CaptureHash(window, "23-timeline-toolbar-compact-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var light = CaptureHash(window, "24-timeline-toolbar-compact-light.png");
            Assert.NotEqual(dark, light);
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();

            var loop = Required<ToggleButton>(window, "LoopToggleButton");
            loop.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            var clearLoop = Required<Button>(window, "ClearLoopButton");
            Assert.True(clearLoop.IsVisible);
            Assert.DoesNotContain("off", Required<TextBlock>(window, "LoopRangeText").Text, StringComparison.OrdinalIgnoreCase);
            CaptureHash(window, "25-timeline-loop-range-dark.png");

            clearLoop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(clearLoop.IsVisible);
            Assert.Equal("Loop: off", Required<TextBlock>(window, "LoopRangeText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Marker_context_menu_opens_from_review_card_padding_and_timeline_then_unmarks()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var mark = Required<Button>(window, "AddMarkerButton");

            mark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1", Required<TextBlock>(window, "DiagnosticCountText").Text);
            var reviewTarget = window.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Classes.Contains("reviewGroupHitTarget") &&
                                  border.DataContext is ReviewGroupRow { CanUnmark: true });
            var blankPoint = reviewTarget.TranslatePoint(
                new Point(Math.Max(2, reviewTarget.Bounds.Width - 3), reviewTarget.Bounds.Height / 2),
                window) ?? throw new InvalidOperationException("Review marker card could not translate its padding point.");
            window.MouseDown(blankPoint, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(blankPoint, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var reviewMenu = Assert.IsType<ContextMenu>(reviewTarget.ContextMenu);
            var reviewUnmark = Assert.Single(reviewMenu.Items.OfType<MenuItem>());
            Assert.StartsWith("Unmark Marker 1", reviewUnmark.Header?.ToString(), StringComparison.Ordinal);
            reviewUnmark.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("0", Required<TextBlock>(window, "DiagnosticCountText").Text);

            mark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var timeline = Required<ReplayTimelineSurface>(window, "ReplayTimelineSurface");
            var timelinePoint = timeline.TranslatePoint(new Point(5, timeline.Bounds.Height / 2), window) ??
                throw new InvalidOperationException("Timeline could not translate its first-frame point.");
            window.MouseDown(timelinePoint, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(timelinePoint, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var timelineMenu = Assert.IsType<ContextMenu>(timeline.ContextMenu);
            var timelineUnmark = Assert.Single(timelineMenu.Items.OfType<MenuItem>());
            Assert.StartsWith("Unmark Marker 1", timelineUnmark.Header?.ToString(), StringComparison.Ordinal);
            timelineUnmark.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("0", Required<TextBlock>(window, "DiagnosticCountText").Text);
            Assert.Contains("Removed marker", Required<TextBlock>(window, "SessionStatusText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Inspector_summarizes_per_byte_acknowledgements_without_losing_the_transport_data()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);

            var rawRecords = Required<ListBox>(window, "RawRecordsList");
            var eventBufferRead = rawRecords.Items
                .OfType<RawRecordRow>()
                .First(row => row.Record.I2c?.Acked.Count == 80);
            rawRecords.SelectedItem = eventBufferRead;
            Dispatcher.UIThread.RunJobs();

            var transport = Required<ItemsControl>(window, "TransportFieldsItemsControl").Items
                .OfType<InspectorDetailRow>()
                .ToDictionary(row => row.Label, row => row.Value, StringComparer.Ordinal);
            Assert.Equal("ACK ×79; final NAK", transport["Data ACK"]);
            Assert.DoesNotContain("ACK ACK ACK", transport["Data ACK"]);
            Assert.Equal(80, eventBufferRead.Record.I2c?.Acked.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Pending_0x97_configuration_names_the_still_active_replay()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            Required<ComboBox>(window, "EventVersionComboBox").SelectedIndex = 4;

            var hint = Required<TextBlock>(window, "ConfigurationHintText").Text ?? string.Empty;
            Assert.Contains("0x97 pending", hint);
            Assert.Contains("active replay: Common 0x83", hint);
            Assert.Equal("Physical #1 · Common 0x83 · Host-state", Required<TextBlock>(window, "InspectorSubtitleText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Theme_toggle_preserves_the_capture_status_summary()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var status = Required<TextBlock>(window, "SessionStatusText");
            var expected = status.Text;

            Required<Button>(window, "ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal(expected, status.Text);
        }
        finally
        {
            window.Close();
        }
    }

    private static byte[] CommonPacket(byte asil, bool corruptCrc, ushort x)
    {
        var packet = new byte[CommonEventBufferDecoder.FrameLength];
        packet.AsSpan(1, CommonEventBufferDecoder.FingerCount * CommonEventBufferDecoder.FingerLength).Fill(0xFF);
        packet[0] = 1;
        packet[1] = (byte)((1 << 4) | (byte)Nvt.Replay.Core.TouchStatus.Move);
        packet[2] = (byte)(x >> 8);
        packet[3] = (byte)x;
        packet[4] = 0x01;
        packet[5] = 0x40;
        packet[6] = 0xFF;
        packet[7] = 0xFF;
        packet.AsSpan(71, 2).Fill(0xFF);
        packet[73] = asil;
        packet[79] = CommonCrc8.Compute(packet.AsSpan(0, 79));
        if (corruptCrc) packet[79] ^= 0xFF;
        return packet;
    }

    private static byte[] MixedTenContactPacket()
    {
        var packet = new byte[CommonEventBufferDecoder.FrameLength];
        packet[0] = CommonEventBufferDecoder.FingerCount;
        for (var index = 0; index < CommonEventBufferDecoder.FingerCount; index++)
        {
            var offset = 1 + (index * CommonEventBufferDecoder.FingerLength);
            var id = (byte)(index + 1);
            var type = index switch
            {
                8 => Nvt.Replay.Core.TouchType.Glove,
                9 => Nvt.Replay.Core.TouchType.Palm,
                _ => Nvt.Replay.Core.TouchType.Finger,
            };
            var x = (ushort)(140 + (index * 170));
            var y = (ushort)(100 + (index * 80));
            packet[offset] = (byte)((id << 4) | ((byte)type << 2) | (byte)Nvt.Replay.Core.TouchStatus.Move);
            packet[offset + 1] = (byte)(x >> 8);
            packet[offset + 2] = (byte)x;
            packet[offset + 3] = (byte)(y >> 8);
            packet[offset + 4] = (byte)y;
            packet[offset + 5] = 0xFF;
            packet[offset + 6] = 0xFF;
        }
        packet[71] = 0xFF;
        packet[72] = 0xFF;
        packet[73] = 0x00;
        packet.AsSpan(74, 5).Fill(0xFF);
        packet[79] = CommonCrc8.Compute(packet.AsSpan(0, 79));
        return packet;
    }

    private static Rect BoundsInside(Control control, Visual ancestor)
    {
        var origin = control.TranslatePoint(default, ancestor) ??
            throw new InvalidOperationException($"{control.Name ?? control.GetType().Name} could not translate into its ancestor.");
        return new Rect(origin, control.Bounds.Size);
    }

    private static async Task<string> WriteKingstVisCaptureAsync(params byte[][] packets)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nvt-inspector-{Guid.NewGuid():N}.csv");
        var lines = new List<string> { "Time [s],Packet ID,Address,Data,Read/Write,ACK" };
        var packetId = 0;
        for (var frameIndex = 0; frameIndex < packets.Length; frameIndex++)
        {
            var writeTime = 1.0 + (frameIndex * 0.1);
            foreach (var value in new byte[] { 0xFF, 0x09, 0x90, 0x00 })
                lines.Add($"{writeTime.ToString("0.000000", CultureInfo.InvariantCulture)},{packetId},0x02,0x{value:X2},Write,ACK");
            packetId++;
            var readTime = writeTime + 0.0002;
            for (var index = 0; index < packets[frameIndex].Length; index++)
            {
                var ack = index == packets[frameIndex].Length - 1 ? "NAK" : "ACK";
                lines.Add($"{readTime.ToString("0.000000", CultureInfo.InvariantCulture)},{packetId},0x03,0x{packets[frameIndex][index]:X2},Read,{ack}");
            }
            packetId++;
        }
        await File.WriteAllLinesAsync(path, lines);
        return path;
    }

    private static MainWindow ShowWindow()
    {
        var window = new MainWindow { Width = 1480, Height = 840 };
        window.Show();
        return window;
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control '{name}'.");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Timed out while waiting for the output preview.");
    }

    private static string CaptureHash(Window window, string artifactName)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Headless renderer returned no frame.");
        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);
        var artifactDirectory = Environment.GetEnvironmentVariable("NVT_UI_AUDIT_DIR");
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            Directory.CreateDirectory(artifactDirectory);
            frame.Save(Path.Combine(artifactDirectory, artifactName), PngBitmapEncoderOptions.Default);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
