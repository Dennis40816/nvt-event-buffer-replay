using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Views;
using Nvt.Replay.Analysis;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Rendering;
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
            Assert.Same(Required<TabItem>(window, "PaintTab"), Required<TabControl>(window, "WorkspaceTabs").SelectedItem);
            Assert.True(Required<Border>(window, "ReplayTransportBorder").IsVisible);
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
            Assert.Equal("Version", version.PlaceholderText);
            Assert.Equal(32, version.Bounds.Height);
            Assert.Equal(32, profile.Bounds.Height);
            Assert.Equal(34, panelWidth.Height);
            Assert.Equal(32, load.Bounds.Height);
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

            Assert.Equal(new GridLength(0), shell.ColumnDefinitions[0].Width);
            Assert.False(reviewContent.IsVisible);
            Assert.Equal(new GridLength(4), shell.ColumnDefinitions[2].Width);
            Assert.Equal(new GridLength(286), shell.ColumnDefinitions[3].Width);
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
            var expectedPaintResolution = $"{Required<TextBox>(window, "PanelWidthTextBox").Text} × {Required<TextBox>(window, "PanelHeightTextBox").Text}";
            Assert.Contains(expectedPaintResolution, Required<TextBlock>(window, "OutputVideoBadgeText").Text);
            Assert.Contains(expectedPaintResolution, Required<ComboBoxItem>(window, "OutputPanelResolutionItem").Content?.ToString());
            Assert.False(Required<Button>(window, "PlayPauseButton").IsEnabled);
            Assert.True(Required<Button>(window, "OutputPreviewPlayPauseButton").IsEnabled);
            Assert.True(Required<ToggleButton>(window, "OutputPreviewRepeatToggleButton").IsChecked);
            Assert.False(Required<Border>(window, "OutputExportWarningPanel").IsVisible);
            Assert.False(Required<Border>(window, "OutputExportActivityPanel").IsVisible);
            var sourceLines = Required<TextBlock>(window, "OutputSourceText").Text!.Split('\n');
            Assert.Equal(32, sourceLines[^2].Length);
            Assert.Equal(32, sourceLines[^1].Length);
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            var outputLight = CaptureHash(window, "05-golden-output-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            var outputDark = CaptureHash(window, "06-golden-output-dark.png");
            Required<Button>(window, "OutputPreviewFullscreenButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "27-golden-output-fullscreen-dark.png");
            Required<Button>(window, "OutputFullscreenExitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var outputContent = Required<ComboBox>(window, "OutputContentComboBox");
            outputContent.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            var heatmapDark = CaptureHash(window, "09-golden-heatmap-dark.png");
            Required<ComboBox>(window, "HeatmapModeComboBox").SelectedIndex = 1;
            Required<TextBox>(window, "HeatmapRepeatedThresholdTextBox").Text = "2";
            Required<TextBox>(window, "HeatmapRepeatedThresholdTextBox").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "28-golden-heatmap-repeated-dark.png");
            Required<ComboBox>(window, "HeatmapModeComboBox").SelectedIndex = 0;
            outputContent.SelectedIndex = 2;
            Dispatcher.UIThread.RunJobs();
            var packageDark = CaptureHash(window, "10-golden-package-dark.png");
            outputContent.SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            var settingsDark = CaptureHash(window, "11-golden-output-settings-dark.png");
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 5;
            Required<ComboBox>(window, "OutputResolutionComboBox").SelectedIndex = 4;
            Dispatcher.UIThread.RunJobs();
            var customSettingsDark = CaptureHash(window, "15-golden-output-custom-settings-dark.png");
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 2;
            Required<ComboBox>(window, "OutputResolutionComboBox").SelectedIndex = 0;
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
            Assert.NotEqual(settingsDark, customSettingsDark);
            Assert.NotEqual(outputDark, compactOutputDark);
            Assert.NotEqual(compactHeatmapDark, compactHeatmapLight);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void Long_MP4_export_gate_uses_frame_count_or_video_duration()
    {
        Assert.False(MainWindow.RequiresLongVideoExport(9_999, TimeSpan.FromSeconds(119)));
        Assert.True(MainWindow.RequiresLongVideoExport(10_000, TimeSpan.FromSeconds(10)));
        Assert.True(MainWindow.RequiresLongVideoExport(100, TimeSpan.FromMinutes(2)));
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
            var flatBytes = Required<Expander>(window, "FlatBytesExpander");
            var sourceIdentity = Required<Expander>(window, "SourceIdentityExpander");
            Assert.False(flatBytes.IsExpanded);
            Assert.False(sourceIdentity.IsExpanded);
            details.SelectedItem = Required<TabItem>(window, "InspectorRawTab");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("inspectorDisclosure", flatBytes.Classes);
            Assert.Contains("inspectorDisclosure", sourceIdentity.Classes);
            Assert.True(flatBytes.Bounds.Width > 250, $"Flat bytes disclosure width was {flatBytes.Bounds.Width:0.##}.");
            Assert.True(sourceIdentity.Bounds.Width > 250, $"Source identity disclosure width was {sourceIdentity.Bounds.Width:0.##}.");
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
            Assert.Equal(280, inspectorColumn.MinWidth);
            Assert.Equal(420, inspectorColumn.MaxWidth);
            Assert.True(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);

            inspectorColumn.Width = new GridLength(400);
            Dispatcher.UIThread.RunJobs();
            var toggle = Required<Button>(window, "InspectorRailToggleButton");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new GridLength(58), inspectorColumn.Width);
            Assert.False(Required<GridSplitter>(window, "InspectorGridSplitter").IsVisible);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(new GridLength(400), inspectorColumn.Width);
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

            Required<Grid>(window, "WorkspaceShellGrid").ColumnDefinitions[3].Width = new GridLength(420);
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "22-inspector-ten-contacts-dark.png");

            Required<Grid>(window, "WorkspaceShellGrid").ColumnDefinitions[3].Width = new GridLength(280);
            contacts.ScrollIntoView(contacts.SelectedItem!);
            Dispatcher.UIThread.RunJobs();
            var inspectorRail = Required<Grid>(window, "InspectorRailContent");
            var selectedContainer = contacts.GetVisualDescendants()
                .OfType<ListBoxItem>()
                .Single(item => item.DataContext is InspectorContactRow { Id: 10 });
            var selectedBounds = BoundsInside(selectedContainer, inspectorRail);
            Assert.True(selectedBounds.Left >= 0);
            Assert.True(selectedBounds.Right <= inspectorRail.Bounds.Width + 0.5);
            CaptureHash(window, "26-inspector-ten-contacts-320-dark.png");

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

            Assert.False(Required<Border>(window, "ReplayTransportBorder").IsVisible);
            Assert.False(Required<Border>(window, "InspectorRailBorder").IsVisible);
            Assert.Equal(0, Required<Grid>(window, "RootLayoutGrid").RowDefinitions[2].Height.Value);
            Assert.Equal(0, Required<Grid>(window, "WorkspaceShellGrid").ColumnDefinitions[3].Width.Value);

            var outputContent = Required<ComboBox>(window, "OutputContentComboBox");
            Assert.Equal(3, outputContent.ItemCount);
            Assert.True(Required<Grid>(window, "OutputVideoPanel").IsVisible);
            Assert.False(Required<Grid>(window, "OutputHeatmapPanel").IsVisible);
            Assert.False(Required<Border>(window, "OutputPackagePanel").IsVisible);
            Assert.True(Required<ToggleButton>(window, "OutputPreviewRepeatToggleButton").IsChecked);

            var videoPanel = Required<Grid>(window, "OutputVideoPanel");
            var settingsRail = Required<Border>(window, "OutputSettingsRailBorder");
            var settingsScroller = Required<ScrollViewer>(window, "OutputSettingsScrollViewer");
            var settingsLeft = BoundsInside(settingsRail, videoPanel).Left;
            Assert.True(BoundsInside(outputContent, window).Left >= BoundsInside(settingsRail, window).Left);
            Assert.True(BoundsInside(Required<Button>(window, "OutputPreviewPlayPauseButton"), videoPanel).Right < settingsLeft);
            Assert.True(BoundsInside(Required<Button>(window, "OutputPreviewFullscreenButton"), videoPanel).Right < settingsLeft);
            var clockBounds = BoundsInside(Required<ComboBox>(window, "OutputClockComboBox"), settingsScroller);
            Assert.True(
                clockBounds.Right <= settingsScroller.Bounds.Width - 12,
                $"Clock selector right edge {clockBounds.Right:0.##} should leave a scrollbar gutter inside {settingsScroller.Bounds.Width:0.##} px.");

            outputContent.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.False(Required<Grid>(window, "OutputVideoPanel").IsVisible);
            var heatmapPanel = Required<Grid>(window, "OutputHeatmapPanel");
            Assert.True(heatmapPanel.IsVisible);
            var heatmapSettings = Required<Border>(window, "HeatmapSettingsRailBorder");
            var heatmapSurface = Required<AnalysisHeatmapSurface>(window, "AnalysisHeatmapPreview");
            var heatmapSettingsLeft = BoundsInside(heatmapSettings, heatmapPanel).Left;
            var heatmapSurfaceRight = BoundsInside(heatmapSurface, heatmapPanel).Right;
            Assert.True(
                heatmapSurfaceRight < heatmapSettingsLeft,
                $"Heatmap preview right edge {heatmapSurfaceRight:0.##} must stay left of settings at {heatmapSettingsLeft:0.##}.");
            Assert.Equal(
                Required<Grid>(window, "OutputPageLayout").ColumnDefinitions[0].Width,
                heatmapPanel.ColumnDefinitions[0].Width);
            Assert.Equal("Export PNG", Required<Button>(window, "ExportSelectedOutputButton").Content);
            var heatmapMode = Required<ComboBox>(window, "HeatmapModeComboBox");
            Assert.Equal(2, heatmapMode.ItemCount);
            Assert.Equal("All recorded points", Assert.IsType<SelectOption>(heatmapMode.SelectedItem).Label);
            heatmapMode.SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Border>(window, "HeatmapRepeatedThresholdPanel").IsVisible);
            var repeatedMinimum = Required<TextBox>(window, "HeatmapRepeatedThresholdTextBox");
            repeatedMinimum.Text = "2";
            repeatedMinimum.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            var labelMinimum = Required<TextBox>(window, "HeatmapLabelThresholdTextBox");
            labelMinimum.Text = "80";
            labelMinimum.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            var heatmap = Required<AnalysisHeatmapSurface>(window, "AnalysisHeatmapPreview");
            Assert.Equal(2, heatmap.MinimumCount);
            Assert.Equal(0.8, heatmap.LabelThresholdRatio, 3);
            Assert.True(heatmap.VisibleCellCount > 0);
            Assert.True(heatmap.VisibleCellCount < 35);

            outputContent.SelectedIndex = 0;
            Assert.True(Required<Grid>(window, "OutputVideoPanel").IsVisible);
            Assert.False(Required<Grid>(window, "OutputHeatmapPanel").IsVisible);
            Assert.Null(window.FindControl<ToggleButton>("OutputSettingsToggleButton"));
            Assert.True(Required<ComboBox>(window, "OutputClockComboBox").IsVisible);
            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 5;
            var customSpeed = Required<TextBox>(window, "OutputCustomSpeedTextBox");
            Assert.True(customSpeed.IsVisible);
            customSpeed.Text = "1.75";
            customSpeed.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputVideoBadgeText").Text?.Contains("1.75×", StringComparison.Ordinal) == true);

            Required<ComboBox>(window, "OutputSpeedComboBox").SelectedIndex = 3;
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputVideoBadgeText").Text?.Contains("2×", StringComparison.Ordinal) == true);
            Assert.Contains("2×", Required<TextBlock>(window, "OutputVideoBadgeText").Text);

            var frameRate = Required<ComboBox>(window, "OutputFrameRateComboBox");
            frameRate.SelectedIndex = 4;
            var customFrameRate = Required<TextBox>(window, "OutputCustomFrameRateTextBox");
            Assert.True(customFrameRate.IsVisible);
            customFrameRate.Text = "180";
            customFrameRate.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputVideoBadgeText").Text?.Contains("180 FPS", StringComparison.Ordinal) == true);

            var fullscreen = Required<Button>(window, "OutputPreviewFullscreenButton");
            Assert.True(fullscreen.IsEnabled);
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);
            Assert.True(Required<Button>(window, "OutputFullscreenPlayPauseButton").IsEnabled);
            Assert.True(Required<OutputVideoTimelineSurface>(window, "OutputFullscreenTimeline").IsEnabled);
            Assert.Equal(WindowState.FullScreen, window.WindowState);
            Required<Button>(window, "OutputFullscreenExitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.False(Required<Border>(window, "OutputFullscreenOverlay").IsVisible);

            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "PaintTab");
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Border>(window, "ReplayTransportBorder").IsVisible);
            Assert.True(Required<Border>(window, "InspectorRailBorder").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Paint_owns_transport_and_independently_toggles_trace_lines_and_report_points()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var tabs = Required<TabControl>(window, "WorkspaceTabs");
            var transport = Required<Border>(window, "ReplayTransportBorder");

            Assert.Equal(2, tabs.SelectedIndex);
            Assert.True(transport.IsVisible);
            tabs.SelectedIndex = 0;
            Assert.False(transport.IsVisible);
            tabs.SelectedIndex = 1;
            Assert.False(transport.IsVisible);
            tabs.SelectedIndex = 2;
            Assert.True(transport.IsVisible);
            tabs.SelectedIndex = 3;
            Assert.False(transport.IsVisible);
            tabs.SelectedIndex = 2;

            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().Last(row => row.Touches > 0);
            Dispatcher.UIThread.RunJobs();
            var paint = Required<ReplayPaintSurface>(window, "PaintSurface");
            Assert.NotEmpty(Assert.IsType<ReplayScene>(paint.CurrentScene).ContactTrails);
            Assert.True(paint.TrailLinesVisible);
            Assert.True(paint.TrailPointsVisible);

            var trace = Required<ToggleButton>(window, "TraceToggleButton");
            var points = Required<ToggleButton>(window, "TrailPointsToggleButton");
            Assert.True(trace.IsChecked);
            Assert.True(points.IsChecked);
            trace.IsChecked = false;
            Assert.False(paint.TrailLinesVisible);
            Assert.True(paint.TrailPointsVisible);
            Assert.NotEmpty(Assert.IsType<ReplayScene>(paint.CurrentScene).ContactTrails);

            points.IsChecked = false;
            Assert.False(paint.TrailPointsVisible);
            Assert.Empty(Assert.IsType<ReplayScene>(paint.CurrentScene).ContactTrails);

            trace.IsChecked = true;
            Assert.True(paint.TrailLinesVisible);
            Assert.False(paint.TrailPointsVisible);
            Assert.NotEmpty(Assert.IsType<ReplayScene>(paint.CurrentScene).ContactTrails);

            points.IsChecked = true;
            Assert.True(paint.TrailPointsVisible);
            Assert.NotEmpty(Assert.IsType<ReplayScene>(paint.CurrentScene).ContactTrails);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Paint_workspace_is_authoritative_and_applies_the_smallest_redraw_for_each_setting()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var decoded = Required<ListBox>(window, "DecodedFramesList");
            decoded.SelectedItem = decoded.Items.OfType<DecodedFrameRow>().Last(row => row.Touches > 0);
            Dispatcher.UIThread.RunJobs();
            var workspace = Assert.IsType<ReplayPaintWorkspace>(window.PaintWorkspace);
            var history = workspace.TrailHistory;
            var paint = Required<ReplayPaintSurface>(window, "PaintSurface");
            var tabs = Required<TabControl>(window, "WorkspaceTabs");
            tabs.SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => window.OutputPreviewPlanIdentity is not null);
            tabs.SelectedItem = Required<TabItem>(window, "PaintTab");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(workspace.Settings.PointView, paint.Mode);
            Assert.Equal(workspace.Settings.TraceVisible, paint.TrailLinesVisible);
            Assert.Equal(workspace.Settings.TrailPointsVisible, paint.TrailPointsVisible);
            Assert.Same(history, workspace.TrailHistory);

            var revision = workspace.Revision;
            var sceneCount = paint.ScenePresentationCount;
            var fitCount = paint.FitCount;
            var planBuildCount = window.OutputPreviewPlanBuildCount;
            Required<TextBox>(window, "PanelWidthTextBox")
                .RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(revision, workspace.Revision);
            Assert.Equal(sceneCount, paint.ScenePresentationCount);
            Assert.Equal(fitCount, paint.FitCount);
            Assert.Equal(planBuildCount, window.OutputPreviewPlanBuildCount);

            Required<ToggleButton>(window, "GridStrengthToggleButton").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(workspace.Settings.StrongGrid);
            Assert.True(paint.StrongGrid);
            Assert.Equal(sceneCount, paint.ScenePresentationCount);
            Assert.Equal(planBuildCount, window.OutputPreviewPlanBuildCount);

            var trailMode = Required<ComboBox>(window, "TrailModeComboBox");
            trailMode.SelectedItem = trailMode.Items
                .OfType<SelectOption>()
                .Single(option => option.Value == nameof(ReplayTrailMode.Recent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ReplayTrailMode.Recent, workspace.Settings.TrailRetention);
            Assert.Equal(++sceneCount, paint.ScenePresentationCount);

            Required<ToggleButton>(window, "TraceToggleButton").IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(workspace.Settings.TraceVisible);
            Assert.Equal(sceneCount, paint.ScenePresentationCount);
            Assert.NotEmpty(Assert.IsType<ReplayScene>(paint.CurrentScene).ContactTrails);

            Required<ToggleButton>(window, "TrailPointsToggleButton").IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(workspace.Settings.TrailPointsVisible);
            Assert.Equal(++sceneCount, paint.ScenePresentationCount);
            Assert.Empty(Assert.IsType<ReplayScene>(paint.CurrentScene).ContactTrails);
            Assert.Same(history, workspace.TrailHistory);

            var width = Required<TextBox>(window, "PanelWidthTextBox");
            width.Text = (workspace.Settings.PanelExtent.MaximumX + 2).ToString(CultureInfo.InvariantCulture);
            width.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(++fitCount, paint.FitCount);
            Assert.Equal(++sceneCount, paint.ScenePresentationCount);
            Assert.Equal(workspace.Settings.PanelExtent, Assert.IsType<ReplayScene>(paint.CurrentScene).Extent);
            Assert.Equal(1, paint.ZoomFactor);
            Assert.Same(history, workspace.TrailHistory);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Prepared_common_decode_keeps_the_raw_first_flow_and_source_object_identity()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            var raw = Required<ListBox>(window, "RawRecordsList");
            var before = raw.Items.OfType<RawRecordRow>().Select(row => row.Record).ToArray();

            Assert.NotEmpty(before);
            Assert.Equal(0, Required<ListBox>(window, "DecodedFramesList").ItemCount);
            Assert.False(Required<TabItem>(window, "PaintTab").IsEnabled);

            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var after = raw.Items.OfType<RawRecordRow>().Select(row => row.Record).ToArray();

            Assert.Equal(19, Required<ListBox>(window, "DecodedFramesList").ItemCount);
            Assert.Equal(before.Length, after.Length);
            for (var index = 0; index < before.Length; index++)
            {
                Assert.Same(before[index], after[index]);
                Assert.Same(before[index].Data, after[index].Data);
                Assert.Same(before[index].SourceFields, after[index].SourceFields);
                Assert.Equal(before[index].StableId, after[index].StableId);
                Assert.Equal(before[index].RawText, after[index].RawText);
                Assert.Equal(before[index].Location, after[index].Location);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Desay_decode_waits_for_explicit_IC_and_Benz_Palm_then_uses_the_typed_path()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "desay97-full-reread.nds.txt");
            await window.OpenCaptureAsync(fixture);

            await window.ApplyStartupDecodeAsync("0x97", "benz-palm");

            Assert.Equal(0, Required<ListBox>(window, "DecodedFramesList").ItemCount);
            Assert.Contains("requires --register-profile", Required<TextBlock>(window, "ConfigurationHintText").Text);

            await window.ApplyStartupDecodeAsync("0x97", "benz-palm", "51927");

            Assert.Equal(2, Required<ListBox>(window, "DecodedFramesList").ItemCount);
            Assert.Contains("Desay 0x97 / Benz Palm", Required<TextBlock>(window, "SessionStatusText").Text);
            Assert.Contains("raw source remains unchanged", Required<TextBlock>(window, "ConfigurationHintText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancelling_a_new_decode_keeps_the_previous_replay_markers_and_ignores_late_progress()
    {
        var window = ShowWindow();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Required<Button>(window, "AddMarkerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal([0], Required<ReplayTimelineSurface>(window, "ReplayTimelineSurface").MarkerFrames);

            window.CaptureDecodeProgressObserver = progress =>
            {
                if (progress.Phase != CaptureDecodePhase.SelectingFormat || entered.IsSet) return;
                entered.Set();
                release.Wait(cancellationToken);
            };
            var cancelledDecode = window.ApplyStartupDecodeAsync("0x84", palmProfile: null);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            Required<Button>(window, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            release.Set();
            await cancelledDecode;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(19, Required<ListBox>(window, "DecodedFramesList").ItemCount);
            Assert.Equal([0], Required<ReplayTimelineSurface>(window, "ReplayTimelineSurface").MarkerFrames);
            Assert.Contains("previous complete result was preserved", Required<TextBlock>(window, "SessionStatusText").Text);
            var retained = Assert.IsType<CommonEventBufferFrame>(
                Required<ListBox>(window, "DecodedFramesList").Items.OfType<DecodedFrameRow>().First().Frame);
            Assert.Equal(CommonEventBufferVersion.V83, retained.Version);
        }
        finally
        {
            release.Set();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Opening_another_capture_supersedes_an_inflight_decode_without_a_stale_commit()
    {
        var window = ShowWindow();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var firstFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            var secondFixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "desay97-full-reread.nds.txt");
            await window.OpenCaptureAsync(firstFixture);
            window.CaptureDecodeProgressObserver = progress =>
            {
                if (progress.Phase != CaptureDecodePhase.SelectingFormat || entered.IsSet) return;
                entered.Set();
                release.Wait(cancellationToken);
            };

            var staleDecode = window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), cancellationToken));
            await window.OpenCaptureAsync(secondFixture);
            release.Set();
            await staleDecode;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("DESAY97-FULL-REREAD.NDS.TXT", Required<TextBlock>(window, "CaptureNameText").Text);
            Assert.Equal(0, Required<ListBox>(window, "DecodedFramesList").ItemCount);
            Assert.False(Required<TabItem>(window, "PaintTab").IsEnabled);
            Assert.Contains("semantic format required", Required<TextBlock>(window, "SessionStatusText").Text);
            Assert.Contains("Confirm the version", Required<TextBlock>(window, "ConfigurationHintText").Text);
        }
        finally
        {
            release.Set();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Output_controls_project_one_workspace_and_no_op_custom_input_keeps_the_plan()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => window.OutputPreviewPlanIdentity is not null);

            Assert.Equal(120, window.OutputSettings.FrameRate);
            Assert.Equal(3, Required<ComboBox>(window, "OutputFrameRateComboBox").SelectedIndex);
            Assert.Contains("120 FPS", Required<TextBlock>(window, "OutputVideoBadgeText").Text);
            Assert.Contains("Frame-paced", Required<TextBlock>(window, "OutputClockDescriptionText").Text);

            var frameRate = Required<ComboBox>(window, "OutputFrameRateComboBox");
            var customFrameRate = Required<TextBox>(window, "OutputCustomFrameRateTextBox");
            frameRate.SelectedIndex = 4;
            await WaitUntilAsync(() => window.OutputPreviewPlanIdentity?.Settings.FrameRate == 180);
            customFrameRate.Text = "120";
            customFrameRate.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            await WaitUntilAsync(() => window.OutputPreviewPlanIdentity?.Settings.FrameRate == 120);

            var revision = window.OutputSettingsRevision;
            var buildCount = window.OutputPreviewPlanBuildCount;
            var identity = window.OutputPreviewPlanIdentity;
            var framePlan = window.OutputPreviewFramePlan;
            customFrameRate.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(revision, window.OutputSettingsRevision);
            Assert.Equal(buildCount, window.OutputPreviewPlanBuildCount);
            Assert.Equal(identity, window.OutputPreviewPlanIdentity);
            Assert.Same(framePlan, window.OutputPreviewFramePlan);

            var finalOptions = window.CreateCurrentOutputExportOptions("final.mp4");
            Assert.NotNull(identity);
            Assert.Equal(identity.Settings.Range, finalOptions.Range);
            Assert.Equal(identity.Settings.Clock, finalOptions.Clock);
            Assert.Equal(identity.Settings.Speed, finalOptions.Speed);
            Assert.Equal(identity.Settings.FrameRate, finalOptions.FrameRate);
            Assert.Equal((identity.Settings.Width, identity.Settings.Height), (finalOptions.Width, finalOptions.Height));

            Required<ComboBox>(window, "OutputClockComboBox").SelectedIndex = 0;
            Assert.Contains("long idle gaps", Required<TextBlock>(window, "OutputClockDescriptionText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Background_output_job_reports_progress_allows_navigation_and_cancels_without_blocking()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var tabs = Required<TabControl>(window, "WorkspaceTabs");
            var outputTab = Required<TabItem>(window, "AnalysisTab");
            tabs.SelectedItem = outputTab;
            await WaitUntilAsync(() => window.OutputPreviewPlanIdentity is not null);

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handle = window.StartOutputExportJob(async (cancellationToken, progress) =>
            {
                progress.Report(new ReplayExportProgress(12, 100, "Encoding MP4"));
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ReplayExportResult(
                    ReplayExportKind.Mp4,
                    "unused.mp4",
                    "unused.manifest.json",
                    100,
                    TimeSpan.FromSeconds(1),
                    null);
            });
            await started.Task;
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputExportStatusText").Text == "Encoding MP4");

            Assert.True(Required<Border>(window, "OutputExportActivityPanel").IsVisible);
            Assert.True(Required<Button>(window, "OutputExportCancelButton").IsEnabled);
            Assert.False(Required<Button>(window, "LoadButton").IsEnabled);
            Assert.Contains("12%", Required<TextBlock>(window, "OutputExportProgressText").Text);

            tabs.SelectedItem = Required<TabItem>(window, "PaintTab");
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<TabItem>(window, "PaintTab").IsSelected);
            tabs.SelectedItem = outputTab;
            Required<ComboBox>(window, "OutputContentComboBox").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Grid>(window, "OutputHeatmapPanel").IsVisible);
            Required<ComboBox>(window, "OutputContentComboBox").SelectedIndex = 0;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Border>(window, "OutputExportActivityPanel").IsVisible);

            Assert.True(window.CancelOutputExportJob());
            var completed = await handle.Completion;
            Assert.Equal(ExportJobStatus.Cancelled, completed.Status);
            await WaitUntilAsync(() => !Required<Border>(window, "OutputExportActivityPanel").IsVisible);
            Assert.True(Required<Button>(window, "LoadButton").IsEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Approved_visual_contract_captures_Paint_and_Output_as_separate_pages()
    {
        var window = ShowWindow();
        try
        {
            window.Width = 1672;
            window.Height = 720;
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            Dispatcher.UIThread.RunJobs();

            var root = Required<Grid>(window, "RootLayoutGrid");
            Assert.Equal(44, root.RowDefinitions[0].Height.Value);
            Assert.Equal(64, root.RowDefinitions[2].Height.Value);
            Assert.Equal(52, Required<Border>(window, "PaintLiveControlsBar").Bounds.Height);
            Assert.Equal(286, Required<Grid>(window, "WorkspaceShellGrid").ColumnDefinitions[3].Width.Value);
            CaptureHash(window, "30-contract-paint-1672x720-dark.png");

            var application = Assert.IsType<App>(Application.Current);
            window.Width = 1920;
            window.Height = 1080;
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "33-contract-paint-1920x1080-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "34-contract-paint-1920x1080-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();

            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputPreviewFrameText").Text != "output 0/0");
            Dispatcher.UIThread.RunJobs();
            var outputLayout = Required<Grid>(window, "OutputPageLayout");
            var settingsRail = Required<Border>(window, "OutputSettingsRailBorder");
            var railFraction = settingsRail.Bounds.Width / outputLayout.Bounds.Width;
            Assert.InRange(railFraction, 0.27, 0.32);
            Assert.False(Required<Border>(window, "ReplayTransportBorder").IsVisible);
            Assert.False(Required<Border>(window, "InspectorRailBorder").IsVisible);
            CaptureHash(window, "35-contract-output-1920x1080-dark.png");
            application.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "36-contract-output-1920x1080-light.png");
            application.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();

            window.Width = 1672;
            window.Height = 720;
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "31-contract-output-1672x720-dark.png");

            window.Width = 1180;
            window.Height = 720;
            Dispatcher.UIThread.RunJobs();
            CaptureHash(window, "32-contract-output-1180x720-dark.png");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Approved_snapshot_matrix_covers_primary_workspaces_themes_and_widths()
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

            VerifySnapshotMatrix(window, "paint");

            Required<TabControl>(window, "WorkspaceTabs").SelectedItem = Required<TabItem>(window, "AnalysisTab");
            await WaitUntilAsync(() => Required<TextBlock>(window, "OutputPreviewFrameText").Text != "output 0/0");
            Dispatcher.UIThread.RunJobs();
            VerifySnapshotMatrix(window, "output");

            Required<ComboBox>(window, "OutputContentComboBox").SelectedIndex = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Grid>(window, "OutputHeatmapPanel").IsVisible);
            VerifySnapshotMatrix(window, "heatmap");

            Required<Button>(window, "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(Required<Control>(window, "SettingsPage").IsVisible);
            VerifySnapshotMatrix(window, "settings");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Playback_shortcuts_are_mapped_and_disclosed_on_transport_controls()
    {
        var window = ShowWindow();
        try
        {
            Assert.Equal(ReplayShortcutAction.TogglePlayback, ReplayShortcutCatalog.Match(Key.Space, KeyModifiers.None)?.Action);
            Assert.Equal(ReplayShortcutAction.PreviousFrame, ReplayShortcutCatalog.Match(Key.Left, KeyModifiers.None)?.Action);
            Assert.Equal(ReplayShortcutAction.NextFrame, ReplayShortcutCatalog.Match(Key.Right, KeyModifiers.None)?.Action);
            Assert.Contains("Space", ToolTip.GetTip(Required<Button>(window, "PlayPauseButton"))?.ToString());
            Assert.Contains("←", ToolTip.GetTip(Required<Button>(window, "PreviousFrameButton"))?.ToString());
            Assert.Contains("→", ToolTip.GetTip(Required<Button>(window, "NextFrameButton"))?.ToString());
            Assert.Contains("Space", ToolTip.GetTip(Required<Button>(window, "OutputPreviewPlayPauseButton"))?.ToString());
            Assert.False(MainWindow.IsShortcutEditingContext(new Button()));
            Assert.False(MainWindow.IsShortcutEditingContext(new ToggleButton()));
            Assert.True(MainWindow.IsShortcutEditingContext(new TextBox()));
            Assert.True(MainWindow.IsShortcutEditingContext(new ComboBox()));
            Assert.True(MainWindow.IsShortcutEditingContext(new Slider()));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Space_preempts_a_focused_toolbar_button_and_toggles_playback()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var speed = Required<ComboBox>(window, "ReplaySpeedComboBox");
            speed.SelectedItem = speed.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "0.01");
            var mark = Required<Button>(window, "AddMarkerButton");
            mark.Focus();
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("0", Required<TextBlock>(window, "DiagnosticCountText").Text);
            Assert.Contains("Pause", Required<Button>(window, "PlayPauseButton").Content?.ToString());

            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Play", Required<Button>(window, "PlayPauseButton").Content?.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Changing_speed_interrupts_the_old_delay_and_one_frame_loop_is_rejected()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var speed = Required<ComboBox>(window, "ReplaySpeedComboBox");
            var speedItems = speed.Items.OfType<ComboBoxItem>().ToArray();
            Assert.Equal(["0.01", "0.02", "0.05"], speedItems.Take(3).Select(item => item.Tag?.ToString() ?? string.Empty).ToArray());
            speed.SelectedItem = speedItems.Single(item => item.Tag?.ToString() == "0.01");
            Required<Button>(window, "PlayPauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var initialFrame = Required<TextBlock>(window, "InspectorLogicalText").Text;

            speed.SelectedItem = speedItems.Single(item => item.Tag?.ToString() == "10");
            await WaitUntilAsync(
                () => Required<TextBlock>(window, "InspectorLogicalText").Text != initialFrame,
                TimeSpan.FromMilliseconds(500));

            if (Required<Button>(window, "PlayPauseButton").Content?.ToString()?.Contains("Pause", StringComparison.Ordinal) == true)
                Required<Button>(window, "PlayPauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Home, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyPress(Key.I, RawInputModifiers.None, PhysicalKey.None, "i");
            window.KeyRelease(Key.I, RawInputModifiers.None, PhysicalKey.None, "i");
            window.KeyPress(Key.O, RawInputModifiers.None, PhysicalKey.None, "o");
            window.KeyRelease(Key.O, RawInputModifiers.None, PhysicalKey.None, "o");
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("1-1", Required<TextBlock>(window, "LoopRangeText").Text);

            Required<Button>(window, "PlayPauseButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Play", Required<Button>(window, "PlayPauseButton").Content?.ToString());
            Assert.Contains("at least 2 frames", Required<TextBlock>(window, "TimelineStatusText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Ten_x_loop_yields_to_the_UI_and_remains_stoppable()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var speed = Required<ComboBox>(window, "ReplaySpeedComboBox");
            speed.SelectedItem = speed.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "10");
            Required<ToggleButton>(window, "LoopToggleButton").IsChecked = true;
            var play = Required<Button>(window, "PlayPauseButton");
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Pause", play.Content?.ToString());
            Assert.StartsWith("Loop: on", Required<TextBlock>(window, "LoopRangeText").Text, StringComparison.OrdinalIgnoreCase);

            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Play", play.Content?.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Rapid_speed_changes_replace_playback_without_leaking_a_stale_runner()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var speed = Required<ComboBox>(window, "ReplaySpeedComboBox");
            var speedItems = speed.Items.OfType<ComboBoxItem>().ToArray();
            var cycle = new[] { "0.01", "10", "0.05", "max", "0.5", "5" }
                .Select(tag => speedItems.Single(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            SettingsControl<CheckBox>(window, "PauseOnAlarmCheckBox").IsChecked = false;
            Required<ToggleButton>(window, "LoopToggleButton").IsChecked = true;
            var play = Required<Button>(window, "PlayPauseButton");
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            for (var index = 0; index < 60; index++)
            {
                speed.SelectedItem = cycle[index % cycle.Length];
                Dispatcher.UIThread.RunJobs();
            }

            speed.SelectedItem = speedItems.Single(item => item.Tag?.ToString() == "0.5");
            Assert.True(
                play.Content?.ToString()?.Contains("Pause", StringComparison.Ordinal) == true,
                $"Playback stopped during rapid changes: {Required<TextBlock>(window, "TimelineStatusText").Text}");
            var initialFrame = Required<TextBlock>(window, "InspectorLogicalText").Text;
            await WaitUntilAsync(
                () => Required<TextBlock>(window, "InspectorLogicalText").Text != initialFrame,
                TimeSpan.FromMilliseconds(750));

            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var stoppedFrame = Required<TextBlock>(window, "InspectorLogicalText").Text;
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();

            Assert.Contains("Play", play.Content?.ToString());
            Assert.Equal(stoppedFrame, Required<TextBlock>(window, "InspectorLogicalText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Auto_pause_stops_on_explicit_break_and_all_break_frames()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            Required<CheckBox>(window, "TransportPauseOnAlarmCheckBox").IsChecked = false;
            Required<CheckBox>(window, "TransportPauseOnBreakCheckBox").IsChecked = true;
            Required<CheckBox>(window, "TransportPauseOnAllBreakCheckBox").IsChecked = false;
            Assert.True(SettingsControl<CheckBox>(window, "PauseOnBreakCheckBox").IsChecked);
            var speed = Required<ComboBox>(window, "ReplaySpeedComboBox");
            speed.SelectedItem = speed.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "max");
            var play = Required<Button>(window, "PlayPauseButton");
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => Required<TextBlock>(window, "TimelineStatusText").Text?.Contains("reported Break", StringComparison.Ordinal) == true,
                TimeSpan.FromSeconds(2));
            Assert.Equal(13, Assert.IsType<ReplayScene>(Required<ReplayPaintSurface>(window, "PaintSurface").CurrentScene).LogicalIndex);

            Required<CheckBox>(window, "TransportPauseOnBreakCheckBox").IsChecked = false;
            Required<CheckBox>(window, "TransportPauseOnAllBreakCheckBox").IsChecked = true;
            Assert.True(SettingsControl<CheckBox>(window, "PauseOnAllBreakCheckBox").IsChecked);
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(
                () => Required<TextBlock>(window, "TimelineStatusText").Text?.Contains("All Break", StringComparison.Ordinal) == true,
                TimeSpan.FromSeconds(2));
            Assert.Equal(18, Assert.IsType<ReplayScene>(Required<ReplayPaintSurface>(window, "PaintSurface").CurrentScene).LogicalIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Playback_fast_path_throttles_expensive_inspector_presentations()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            SettingsControl<CheckBox>(window, "PauseOnAlarmCheckBox").IsChecked = false;
            Required<ToggleButton>(window, "LoopToggleButton").IsChecked = true;
            var speed = Required<ComboBox>(window, "ReplaySpeedComboBox");
            speed.SelectedItem = speed.Items.OfType<ComboBoxItem>().Single(item => item.Tag?.ToString() == "max");
            var play = Required<Button>(window, "PlayPauseButton");
            var before = window.DetailPresentationCount;

            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(280);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Pause", play.Content?.ToString());
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var presentations = window.DetailPresentationCount - before;
            Assert.InRange(presentations, 2, 6);
            Assert.Contains("Play", play.Content?.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Paint_swap_XY_transposes_only_the_view_scene()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var surface = Required<ReplayPaintSurface>(window, "PaintSurface");
            var original = Assert.IsType<ReplayScene>(surface.CurrentScene);
            var sourceContact = Assert.Single(original.ReportedContacts);
            var toggle = Required<ToggleButton>(window, "SwapAxesToggleButton");

            Assert.False(toggle.IsChecked);
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            var swapped = Assert.IsType<ReplayScene>(surface.CurrentScene);
            var viewCoordinates = swapped.ViewCoordinates(sourceContact.X, sourceContact.Y);
            Assert.True(swapped.SwapAxes);
            Assert.Equal(original.Extent, swapped.Extent);
            Assert.Equal(original.Extent.MaximumY, swapped.ViewExtent.MaximumX);
            Assert.Equal(sourceContact.Y, viewCoordinates.X);
            Assert.Equal(sourceContact.X, viewCoordinates.Y);
            Assert.Equal(sourceContact.X, Assert.Single(swapped.ReportedContacts).X);
            Assert.Equal(sourceContact.Y, Assert.Single(swapped.ReportedContacts).Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Heatmap_surface_filters_cells_uses_a_thermal_scale_and_exposes_hover_details()
    {
        var surface = new AnalysisHeatmapSurface();
        var window = new Window { Width = 640, Height = 360, Content = surface };
        window.Show();
        try
        {
            surface.Show(
                new AnalysisHotspot(
                    4,
                    2,
                    [0, 1, 2, 4, 3, 0, 8, 1],
                    19,
                    new AnalysisCoordinateTransform("panel", 0, 2048, 0, 1280)),
                minimumCount: 3,
                labelThresholdRatio: 0.5);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(3, surface.MinimumCount);
            Assert.Equal(15, surface.VisibleSampleCount);
            Assert.Equal(3, surface.VisibleCellCount);
            Assert.Equal(8, surface.PeakCount);
            Assert.NotEqual(AnalysisHeatmapSurface.ColorAt(0), AnalysisHeatmapSurface.ColorAt(1));

            var point = surface.TranslatePoint(new Point(352, 242), window) ??
                throw new InvalidOperationException("Heatmap could not translate its hover point.");
            window.MouseMove(point);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(surface.HoveredCell);
            Assert.True(surface.HoveredCell!.Count >= 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Timeline_seek_request_updates_the_nearest_logical_frame()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Dispatcher.UIThread.RunJobs();

            var timeline = Required<Control>(window, "ReplayTimelineSurface");
            typeof(MainWindow)
                .GetMethod("ReplayTimelineSurface_OnSeekRequested", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [timeline, new ReplayTimelineSeekEventArgs(14)]);

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
            var groups = new Control[] { playback, timing, annotation };
            var bounds = groups.Select(control => BoundsInside(control, bar)).ToArray();

            Assert.All(bounds, rect =>
            {
                Assert.True(rect.Left >= 0);
                Assert.True(rect.Right <= bar.Bounds.Width + 0.5);
                Assert.InRange(rect.Center.Y, (bar.Bounds.Height / 2) - 1, (bar.Bounds.Height / 2) + 1);
            });
            Assert.True(bounds[0].Right < bounds[1].Left);
            Assert.True(bounds[1].Right < bounds[2].Left);

            var timeline = BoundsInside(Required<Control>(window, "ReplayTimelineSurface"), bar);
            var currentClock = BoundsInside(Required<TextBlock>(window, "ReplayClockText"), bar);
            var endClock = BoundsInside(Required<TextBlock>(window, "ReplayEndClockText"), bar);
            var counts = BoundsInside(Required<StackPanel>(window, "TimelineTrackLabels"), bar);
            Assert.False(Required<TextBlock>(window, "TimelineStatusText").IsVisible);
            Assert.False(Required<TextBlock>(window, "LoopRangeText").IsVisible);
            Assert.True(Required<TextBlock>(window, "ReplayClockText").IsVisible);
            Assert.True(Required<TextBlock>(window, "ReplayEndClockText").IsVisible);
            Assert.True(Required<StackPanel>(window, "TimelineTrackLabels").IsVisible);
            Assert.True(currentClock.Right < endClock.Left);
            Assert.True(endClock.Right < counts.Left);
            Assert.True(timeline.Right < counts.Left);
            Assert.True(counts.Right <= bar.Bounds.Width + 0.5);

            var save = Required<Button>(window, "SaveReviewButton");
            var loadReview = Required<Button>(window, "LoadReviewButton");
            var saveIcon = Required<TextBlock>(window, "SaveActionIcon");
            var loadReviewIcon = Required<TextBlock>(window, "LoadReviewActionIcon");
            Assert.True(save.IsEnabled);
            Assert.True(loadReview.IsEnabled);
            Assert.True(saveIcon.IsVisible);
            Assert.True(loadReviewIcon.IsVisible);
            Assert.True(saveIcon.Bounds.Width > 0);
            Assert.True(loadReviewIcon.Bounds.Width > 0);
            Assert.False(Required<TextBlock>(window, "SaveActionLabel").IsVisible);
            Assert.False(Required<TextBlock>(window, "LoadReviewActionLabel").IsVisible);
            var reviewActions = Required<StackPanel>(window, "ReviewHeaderActions");
            var saveBounds = BoundsInside(save, reviewActions);
            var loadReviewBounds = BoundsInside(loadReview, reviewActions);
            Assert.True(saveBounds.Right <= loadReviewBounds.Left);
            Assert.True(loadReviewBounds.Right <= reviewActions.Bounds.Width + 0.5);

            save.IsEnabled = false;
            loadReview.IsEnabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(IsTransparent(save.Background), $"Disabled Save background must be transparent, got {save.Background}.");
            Assert.True(IsTransparent(loadReview.Background), $"Disabled Open review background must be transparent, got {loadReview.Background}.");
            Assert.All(
                new[] { save, loadReview },
                button => Assert.True(
                    IsTransparent(button.GetVisualDescendants().OfType<ContentPresenter>().Single().Background),
                    $"Disabled {button.Name} presenter must remain transparent."));
            save.IsEnabled = true;
            loadReview.IsEnabled = true;
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("38", Required<TextBlock>(window, "TimelinePhysicalCountText").Text);
            Assert.Equal("19", Required<TextBlock>(window, "TimelineLogicalCountText").Text);
            Assert.Equal("0", Required<TextBlock>(window, "TimelineEvidenceCountText").Text);
            Assert.Null(window.FindControl<TextBlock>("TimelineSummaryText"));
            Assert.Null(window.FindControl<Button>("ClearLoopButton"));

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
            Assert.StartsWith("Loop: on", Required<TextBlock>(window, "LoopRangeText").Text, StringComparison.OrdinalIgnoreCase);
            CaptureHash(window, "25-timeline-loop-range-dark.png");

            loop.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
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
            Assert.Equal([0], Required<ReplayTimelineSurface>(window, "ReplayTimelineSurface").MarkerFrames);
            Required<Button>(window, "ReviewRailToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
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
            Assert.Null(reviewTarget.ContextMenu);

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
            Assert.Null(timeline.ContextMenu);

            var emptyFramePoint = timeline.TranslatePoint(
                new Point(Math.Max(5, timeline.Bounds.Width - 5), timeline.Bounds.Height / 2),
                window) ?? throw new InvalidOperationException("Timeline could not translate its empty-frame point.");
            window.MouseDown(emptyFramePoint, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(emptyFramePoint, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(timeline.ContextMenu);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Timeline_marker_menu_removes_only_the_chosen_marker_when_points_overlap()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var mark = Required<Button>(window, "AddMarkerButton");
            mark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Required<ToggleButton>(window, "LoopToggleButton").IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            mark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2", Required<TextBlock>(window, "DiagnosticCountText").Text);

            var timeline = Required<ReplayTimelineSurface>(window, "ReplayTimelineSurface");
            var firstFramePoint = timeline.TranslatePoint(new Point(5, timeline.Bounds.Height / 2), window) ??
                throw new InvalidOperationException("Timeline could not translate its first-frame point.");
            window.MouseDown(firstFramePoint, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(firstFramePoint, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            var overlapMenu = Assert.IsType<ContextMenu>(timeline.ContextMenu);
            var overlapItems = overlapMenu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(2, overlapItems.Length);
            var secondMarker = Assert.Single(overlapItems, item =>
                item.Header?.ToString()?.StartsWith("Unmark Marker 2 · frame 1", StringComparison.Ordinal) == true);
            secondMarker.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("1", Required<TextBlock>(window, "DiagnosticCountText").Text);
            Assert.Null(timeline.ContextMenu);

            window.MouseDown(firstFramePoint, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(firstFramePoint, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var remainingItem = Assert.Single(Assert.IsType<ContextMenu>(timeline.ContextMenu).Items.OfType<MenuItem>());
            Assert.StartsWith("Unmark Marker 1 · frame 1", remainingItem.Header?.ToString(), StringComparison.Ordinal);
            timeline.ContextMenu?.Close();
            timeline.ContextMenu = null;
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Continuous_timeline_seek_is_latest_wins_and_a_final_seek_cancels_stale_work()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            Dispatcher.UIThread.RunJobs();

            var timeline = Required<Control>(window, "ReplayTimelineSurface");
            var handler = typeof(MainWindow).GetMethod(
                "ReplayTimelineSurface_OnSeekRequested",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            handler.Invoke(window, [timeline, new ReplayTimelineSeekEventArgs(4, isContinuous: true)]);
            handler.Invoke(window, [timeline, new ReplayTimelineSeekEventArgs(9, isContinuous: true)]);
            handler.Invoke(window, [timeline, new ReplayTimelineSeekEventArgs(17, isContinuous: true)]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(
                17,
                Assert.IsType<ReplayScene>(Required<ReplayPaintSurface>(window, "PaintSurface").CurrentScene).LogicalIndex);

            handler.Invoke(window, [timeline, new ReplayTimelineSeekEventArgs(3, isContinuous: true)]);
            handler.Invoke(window, [timeline, new ReplayTimelineSeekEventArgs(14)]);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("15 / 19", Required<TextBlock>(window, "InspectorLogicalText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Mark_is_always_one_frame_and_can_be_renamed_or_cleared()
    {
        var window = ShowWindow();
        try
        {
            var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
            await window.OpenCaptureAsync(fixture);
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var workspace = Assert.IsType<ReplayPaintWorkspace>(window.PaintWorkspace);
            var history = workspace.TrailHistory;

            Required<ToggleButton>(window, "LoopToggleButton").IsChecked = true;
            var mark = Required<Button>(window, "AddMarkerButton");
            mark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            var markerRow = Assert.Single(Required<ListBox>(window, "DiagnosticListBox").Items.OfType<ReviewGroupRow>());
            Assert.Contains("frame 1", markerRow.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("frames 1-", markerRow.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, workspace.AnnotationRevision);
            Assert.Equal("Marker 1", Assert.Single(
                Assert.IsType<ReplayScene>(Required<ReplayPaintSurface>(window, "PaintSurface").CurrentScene).MarkerLabels));
            Assert.Same(history, workspace.TrailHistory);

            var name = Required<TextBox>(window, "MarkerNameTextBox");
            name.Text = "Startup touch";
            Required<Button>(window, "RenameMarkerButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            markerRow = Assert.Single(Required<ListBox>(window, "DiagnosticListBox").Items.OfType<ReviewGroupRow>());
            Assert.Contains("Startup touch", markerRow.Message, StringComparison.Ordinal);
            Assert.Equal("Renamed marker · Startup touch", Required<TextBlock>(window, "SessionStatusText").Text);
            Assert.Equal(2, workspace.AnnotationRevision);
            Assert.Equal("Startup touch", Assert.Single(
                Assert.IsType<ReplayScene>(Required<ReplayPaintSurface>(window, "PaintSurface").CurrentScene).MarkerLabels));

            mark.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2", Required<TextBlock>(window, "DiagnosticCountText").Text);
            var clear = Required<Button>(window, "ClearMarkersButton");
            Assert.True(clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("0", Required<TextBlock>(window, "DiagnosticCountText").Text);
            Assert.False(clear.IsEnabled);
            Assert.Equal("Cleared 2 markers", Required<TextBlock>(window, "SessionStatusText").Text);
            Assert.Equal(4, workspace.AnnotationRevision);
            Assert.Empty(Assert.IsType<ReplayScene>(
                Required<ReplayPaintSurface>(window, "PaintSurface").CurrentScene).MarkerLabels);
            Assert.Same(history, workspace.TrailHistory);
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
    public void Settings_page_owns_low_frequency_preferences_without_removing_live_page_controls()
    {
        var window = ShowWindow();
        try
        {
            var settings = Required<Control>(window, "SettingsPage");
            Assert.False(settings.IsVisible);
            Required<Button>(window, "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.True(settings.IsVisible);
            Assert.True(SettingsControl<ItemsControl>(window, "SettingsShortcutModulesItemsControl").ItemCount > 0);
            Assert.True(SettingsControl<CheckBox>(window, "PauseOnAlarmCheckBox").IsChecked);
            Assert.True(SettingsControl<RadioButton>(window, "DefaultFramePacedRadioButton").IsChecked);
            var playbackNav = SettingsControl<Button>(window, "PlaybackNavButton");
            playbackNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("active", playbackNav.Classes);
            Assert.DoesNotContain("active", SettingsControl<Button>(window, "GeneralNavButton").Classes);
            Assert.NotNull(Required<ComboBox>(window, "PaintModeComboBox"));
            Assert.NotNull(Required<ComboBox>(window, "TrailModeComboBox"));
            Assert.NotNull(Required<ComboBox>(window, "OutputClockComboBox"));
            Assert.NotNull(Required<ComboBox>(window, "OutputFrameRateComboBox"));
            Assert.Null(window.FindControl<Button>("ShortcutHelpButton"));
            CaptureHash(window, "29-settings-dark.png");

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(settings.IsVisible);
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

            Required<Button>(window, "SettingsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            SettingsControl<Button>(window, "ThemeButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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
        if (Application.Current is { } application)
            application.RequestedThemeVariant = ThemeVariant.Dark;

        var window = new MainWindow
        {
            Width = 1480,
            Height = 840,
            WindowState = WindowState.Normal
        };
        window.Show();
        VisualTestCapture.Stabilize(window);
        return window;
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.FindControl<T>(name) ?? throw new InvalidOperationException($"Missing control '{name}'.");

    private static T SettingsControl<T>(MainWindow window, string name) where T : Control =>
        Required<SettingsView>(window, "SettingsPage").FindControl<T>(name) ??
        throw new InvalidOperationException($"Missing Settings control '{name}'.");

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? waitTimeout = null)
    {
        var timeout = DateTime.UtcNow + (waitTimeout ?? TimeSpan.FromSeconds(5));
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(predicate(), "Timed out while waiting for the output preview.");
    }

    private static bool IsTransparent(IBrush? brush) =>
        brush is null || brush is ISolidColorBrush { Color.A: 0 };

    private static void VerifySnapshotMatrix(MainWindow window, string workspace)
    {
        var application = Assert.IsType<App>(Application.Current);
        foreach (var snapshot in new[]
                 {
                     (Width: 1920d, Height: 1080d, Theme: ThemeVariant.Dark, ThemeName: "dark"),
                     (Width: 1920d, Height: 1080d, Theme: ThemeVariant.Light, ThemeName: "light"),
                     (Width: 1180d, Height: 720d, Theme: ThemeVariant.Dark, ThemeName: "dark"),
                     (Width: 1180d, Height: 720d, Theme: ThemeVariant.Light, ThemeName: "light")
                 })
        {
            window.Width = snapshot.Width;
            window.Height = snapshot.Height;
            application.RequestedThemeVariant = snapshot.Theme;
            VisualTestCapture.ProcessSnapshot(
                window,
                $"{workspace}-{snapshot.Width:0}x{snapshot.Height:0}-{snapshot.ThemeName}.png");
        }
    }

    private static string CaptureHash(Window window, string artifactName) =>
        VisualTestCapture.CaptureHash(window, artifactName);
}
