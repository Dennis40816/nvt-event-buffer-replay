// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Nvt.Replay.Avalonia.ViewModels;
using Xunit;
using static Nvt.Replay.Avalonia.Tests.WorkspaceSnapshotTestHelpers;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowInspectorSelectionTests
{
    [AvaloniaFact]
    public async Task LoadingAnotherCaptureReplacesDecodedSelectionWithItsRawPresentation()
    {
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        try
        {
            window.Show();
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);
            var frames = Required<ListBox>(window, "DecodedFramesList");
            frames.SelectedItem = frames.Items.OfType<DecodedFrameRow>().First(row => row.Touches == 3);
            Assert.Equal("3", Required<TextBlock>(window, "InspectorFingerText").Text);
            Assert.NotEqual("-", Required<TextBlock>(window, "InspectorLogicalText").Text);

            await window.OpenCaptureAsync(Fixture("nds-register-map.nds.txt"));

            var selected = Assert.IsType<RawRecordRow>(Required<ListBox>(window, "RawRecordsList").SelectedItem);
            Assert.Equal(selected.Record.StableId, Required<TextBlock>(window, "StableIdText").Text);
            Assert.Equal("-", Required<TextBlock>(window, "InspectorLogicalText").Text);
            Assert.Equal("0", Required<TextBlock>(window, "InspectorFingerText").Text);
            Assert.Equal("0", Required<TextBlock>(window, "InspectorGloveText").Text);
            Assert.Equal("0", Required<TextBlock>(window, "InspectorPalmText").Text);
            Assert.Equal("0 active", Required<TextBlock>(window, "InspectorContactCountText").Text);
            Assert.Equal(0, Required<ListBox>(window, "InspectorContactsList").ItemCount);
            Assert.False(Required<ListBox>(window, "InspectorContactsList").IsVisible);
            Assert.Equal("-", Required<TextBlock>(window, "InspectorCrcText").Text);
            Assert.Equal("-", Required<TextBlock>(window, "InspectorAsilText").Text);
            Assert.Equal(string.Empty, Required<TextBlock>(window, "InspectorAsilRawText").Text);
            Assert.False(Required<Border>(window, "InspectorAllBreakBadge").IsVisible);
            Assert.Equal("0", Required<TextBlock>(window, "CollapsedFingerText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CopySummaryWithoutSelectionLeavesClipboardAndStatusUnchanged()
    {
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        try
        {
            window.Show();
            var clipboard = TopLevel.GetTopLevel(window)?.Clipboard ??
                throw new InvalidOperationException("Headless clipboard is unavailable.");
            await clipboard.SetTextAsync("Existing clipboard text");
            var status = Required<TextBlock>(window, "SessionStatusText").Text;

            RaiseClick(Required<Button>(window, "CopyInspectorButton"));

            Assert.Equal("Existing clipboard text", await clipboard.TryGetTextAsync());
            Assert.Equal(status, Required<TextBlock>(window, "SessionStatusText").Text);
        }
        finally
        {
            window.Close();
        }
    }
}
