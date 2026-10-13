using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

/// <summary>
/// Pins the legend, trail visibility and pan behavior of <see cref="ReplayPaintSurface"/> through its
/// public API and pointer input, so the state behind them can move without changing what is drawn.
/// </summary>
public sealed class ReplayPaintSurfaceInteractionTests
{
    // The collapsed legend sits 12 px inside the top-left corner of the fitted 544 x 408 viewport.
    private static Point LegendPoint => new(80, 55);

    private static Point FarPoint => new(600, 440);

    private static Point GripPoint => new(300, 300);

    [AvaloniaFact]
    public void Legend_defaults_and_auto_position_resolve_to_top_left_collapsed()
    {
        var surface = new ReplayPaintSurface();

        Assert.True(surface.LegendVisible);
        Assert.True(surface.LegendCollapsed);
        Assert.Equal(ReplayLegendPosition.TopLeft, surface.LegendPosition);
        Assert.True(surface.TrailLinesVisible);
        Assert.True(surface.TrailPointsVisible);

        surface.SetLegendPosition(ReplayLegendPosition.BottomRight);
        Assert.Equal(ReplayLegendPosition.BottomRight, surface.LegendPosition);
        surface.SetLegendPosition(ReplayLegendPosition.Auto);
        Assert.Equal(ReplayLegendPosition.TopLeft, surface.LegendPosition);
    }

    [AvaloniaFact]
    public void Trail_visibility_setter_updates_both_flags()
    {
        var surface = new ReplayPaintSurface();

        surface.SetTrailVisibility(showLines: false, showPoints: true);
        Assert.False(surface.TrailLinesVisible);
        Assert.True(surface.TrailPointsVisible);

        surface.SetTrailVisibility(showLines: true, showPoints: false);
        Assert.True(surface.TrailLinesVisible);
        Assert.False(surface.TrailPointsVisible);
    }

    [AvaloniaFact]
    public void Collapsed_setter_raises_the_event_only_on_change()
    {
        var surface = new ReplayPaintSurface();
        var raised = 0;
        surface.LegendCollapsedChanged += (_, _) => raised++;

        surface.SetLegendCollapsed(true);
        Assert.Equal(0, raised);
        surface.SetLegendCollapsed(false);
        Assert.Equal(1, raised);
        Assert.False(surface.LegendCollapsed);
        surface.SetLegendCollapsed(false);
        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void Clicking_the_legend_toggles_collapsed_and_raises_the_event()
    {
        using var host = new Host();
        var raised = 0;
        host.Surface.LegendCollapsedChanged += (_, _) => raised++;
        host.Frame();

        host.Window.MouseDown(LegendPoint, MouseButton.Left);
        host.Window.MouseUp(LegendPoint, MouseButton.Left);

        Assert.False(host.Surface.LegendCollapsed);
        Assert.Equal(1, raised);
    }

    [AvaloniaFact]
    public void Clicking_outside_the_legend_leaves_it_collapsed()
    {
        using var host = new Host();
        host.Frame();

        host.Window.MouseDown(FarPoint, MouseButton.Left);
        host.Window.MouseUp(FarPoint, MouseButton.Left);

        Assert.True(host.Surface.LegendCollapsed);
    }

    [AvaloniaFact]
    public void Hovering_the_legend_changes_the_frame_and_leaving_restores_it()
    {
        using var host = new Host();
        var resting = host.Frame();

        host.Window.MouseMove(LegendPoint);
        var hovered = host.Frame();
        host.Window.MouseMove(FarPoint);
        var left = host.Frame();

        Assert.NotEqual(resting, hovered);
        Assert.Equal(resting, left);
    }

    [AvaloniaFact]
    public void Hiding_and_showing_the_legend_clears_an_active_hover()
    {
        using var host = new Host();
        var resting = host.Frame();
        host.Window.MouseMove(LegendPoint);
        Assert.NotEqual(resting, host.Frame());

        host.Surface.SetLegendVisible(false);
        host.Frame();
        host.Surface.SetLegendVisible(true);

        Assert.Equal(resting, host.Frame());
    }

    [AvaloniaFact]
    public void Leaving_the_surface_clears_the_hover()
    {
        using var host = new Host();
        var resting = host.Frame();
        host.Window.MouseMove(LegendPoint);
        Assert.NotEqual(resting, host.Frame());

        host.Window.MouseMove(new Point(-20, -20));

        Assert.Equal(resting, host.Frame());
    }
    [AvaloniaFact]
    public void Hidden_legend_ignores_hover_and_click()
    {
        using var host = new Host();
        host.Surface.SetLegendVisible(false);
        var hidden = host.Frame();

        host.Window.MouseMove(LegendPoint);
        var hovered = host.Frame();
        host.Window.MouseDown(LegendPoint, MouseButton.Left);
        host.Window.MouseUp(LegendPoint, MouseButton.Left);

        Assert.Equal(hidden, hovered);
        Assert.True(host.Surface.LegendCollapsed);
        Assert.False(host.Surface.LegendVisible);
    }

    [AvaloniaFact]
    public void Dragging_without_zoom_does_not_pan()
    {
        using var host = new Host();
        var before = host.Frame();

        host.Window.MouseDown(GripPoint, MouseButton.Left);
        host.Window.MouseMove(new Point(GripPoint.X + 40, GripPoint.Y + 30));
        var during = host.Frame();
        host.Window.MouseUp(new Point(GripPoint.X + 40, GripPoint.Y + 30), MouseButton.Left);

        Assert.Equal(before, during);
        Assert.Null(host.Surface.Cursor);
    }

    [AvaloniaFact]
    public void Dragging_when_zoomed_moves_the_view_and_keeps_it_after_release()
    {
        using var host = new Host();
        var fitted = host.Frame();
        for (var step = 0; step < 4; step++) host.Surface.ZoomIn();
        var zoomed = host.Frame();
        Assert.NotNull(host.Surface.Cursor);

        host.Window.MouseDown(GripPoint, MouseButton.Left);
        host.Window.MouseMove(new Point(GripPoint.X - 40, GripPoint.Y - 30));
        var dragged = host.Frame();
        host.Window.MouseUp(new Point(GripPoint.X - 40, GripPoint.Y - 30), MouseButton.Left);
        var released = host.Frame();
        host.Window.MouseMove(new Point(GripPoint.X, GripPoint.Y));
        var afterRelease = host.Frame();

        Assert.NotEqual(zoomed, dragged);
        Assert.Equal(dragged, released);
        Assert.Equal(dragged, afterRelease);

        host.Surface.Fit();
        Assert.Equal(fitted, host.Frame());
    }

    [AvaloniaFact]
    public void Clearing_the_surface_ends_a_pan_in_progress()
    {
        using var host = new Host();
        for (var step = 0; step < 4; step++) host.Surface.ZoomIn();
        host.Frame();
        host.Window.MouseDown(GripPoint, MouseButton.Left);
        host.Window.MouseMove(new Point(GripPoint.X - 40, GripPoint.Y - 30));

        host.Surface.Clear();
        host.Surface.Show(Scene());
        var cleared = host.Frame();
        host.Window.MouseMove(new Point(GripPoint.X - 100, GripPoint.Y - 90));
        var afterMove = host.Frame();
        host.Window.MouseUp(new Point(GripPoint.X - 100, GripPoint.Y - 90), MouseButton.Left);

        Assert.Equal(cleared, afterMove);
    }

    private static ReplayScene Scene() => new(
        0,
        3,
        new ReplayTimelineEntry(
            0,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1),
            false,
            false),
        [],
        [new ReplayContact(1, TouchType.Finger, TouchStatus.Move, 320, 240, "source-0")],
        false,
        new ReplayExtent(640, 480),
        [],
        [],
        [],
        false,
        false,
        false);

    private sealed class Host : IDisposable
    {
        internal Host()
        {
            Surface = new ReplayPaintSurface();
            Window = new Window { Width = 640, Height = 480, Content = Surface };
            Window.Show();
            Surface.Show(Scene());
        }

        internal ReplayPaintSurface Surface { get; }

        internal Window Window { get; }

        internal byte[] Frame()
        {
            using var frame = Window.CaptureRenderedFrame() ??
                throw new InvalidOperationException("Headless frame capture failed.");
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }

        public void Dispose() => Window.Close();
    }
}
