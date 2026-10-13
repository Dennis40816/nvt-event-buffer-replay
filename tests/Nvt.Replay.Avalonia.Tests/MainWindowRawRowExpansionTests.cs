// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Nvt.Replay.Avalonia.ViewModels;
using Xunit;
using static Nvt.Replay.Avalonia.Tests.WorkspaceSnapshotTestHelpers;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowRawRowExpansionTests
{
    [AvaloniaFact]
    public async Task FilteringTheExpandedRowAwayCollapsesItAndAnotherClickExpandsItAgain()
    {
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        try
        {
            window.Show();
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await WaitUntilAsync(() => true);
            var raw = Required<ListBox>(window, "RawRecordsList");
            var row = raw.Items.OfType<RawRecordRow>().First();

            await ClickRawRowAsync(window, raw, row);
            Assert.True(row.IsExpanded);

            Required<TextBox>(window, "RegisterSearchTextBox").Text = "no record contains this text";
            await WaitUntilAsync(() => true);
            Assert.Equal(0, raw.ItemCount);
            Assert.False(row.IsExpanded);

            Required<TextBox>(window, "RegisterSearchTextBox").Text = string.Empty;
            await WaitUntilAsync(() => true);
            Assert.False(row.IsExpanded);
            await ClickRawRowAsync(window, raw, row);
            Assert.True(row.IsExpanded);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LoadingAnotherCaptureCollapsesTheExpandedRowOfTheClearedSession()
    {
        var window = new MainWindow { Width = 1480, Height = 840, WindowState = WindowState.Normal };
        try
        {
            window.Show();
            await window.OpenCaptureAsync(Fixture("kingstvis-common-0x83.csv"));
            await WaitUntilAsync(() => true);
            var raw = Required<ListBox>(window, "RawRecordsList");
            var row = raw.Items.OfType<RawRecordRow>().First();
            await ClickRawRowAsync(window, raw, row);
            Assert.True(row.IsExpanded);

            await window.OpenCaptureAsync(Fixture("nds-register-map.nds.txt"));
            await WaitUntilAsync(() => true);

            Assert.False(row.IsExpanded);
            Assert.All(raw.Items.OfType<RawRecordRow>(), item => Assert.False(item.IsExpanded));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task ClickRawRowAsync(MainWindow window, ListBox raw, RawRecordRow row)
    {
        VisualTestCapture.Stabilize(window);
        var border = raw.GetVisualDescendants()
            .OfType<Border>()
            .Single(item => item.Classes.Contains("rawRecordRow") && ReferenceEquals(item.DataContext, row));
        var point = border.TranslatePoint(new Point(12, 12), window) ??
            throw new InvalidOperationException("Raw record row could not translate into the window.");
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        await WaitUntilAsync(() => true);
    }
}
