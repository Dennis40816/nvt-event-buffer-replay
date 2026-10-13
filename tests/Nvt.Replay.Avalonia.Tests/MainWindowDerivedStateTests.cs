// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Views;
using Xunit;
using static Nvt.Replay.Avalonia.Tests.WorkspaceSnapshotTestHelpers;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class MainWindowDerivedStateTests
{
    [AvaloniaFact]
    public void ThemeToggleAlternatesBetweenLightAndDark()
    {
        var window = ShowWindow(1180, 720, ThemeVariant.Dark);
        try
        {
            var toggle = Required<SettingsView>(window, "SettingsPage").FindControl<Button>("ThemeButton")!;
            var application = Application.Current!;

            RaiseClick(toggle);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);

            RaiseClick(toggle);
            Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);

            RaiseClick(toggle);
            Assert.Equal(ThemeVariant.Light, application.RequestedThemeVariant);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ReviewListsHoldTheRowArraysTheWindowReadsBack()
    {
        var window = ShowWindow(1480, 840, ThemeVariant.Dark);
        try
        {
            await window.OpenCaptureAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv"));
            await window.ApplyStartupDecodeAsync("0x83", palmProfile: null);

            var diagnostics = Required<ListBox>(window, "DiagnosticListBox");
            var markers = Required<ListBox>(window, "PaintMarkerListBox");
            Assert.IsType<ReviewGroupRow[]>(diagnostics.ItemsSource);
            Assert.IsType<PaintMarkerRow[]>(markers.ItemsSource);
            Assert.Equal(
                ((ReviewGroupRow[])diagnostics.ItemsSource!).Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Required<TextBlock>(window, "DiagnosticCountText").Text);
        }
        finally
        {
            window.Close();
        }
    }
}
