using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering;
using Avalonia.Threading;
using Nvt.Replay.Avalonia.Controls;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class ReplayTimelineSurfaceTests
{
    [AvaloniaFact]
    public void Right_click_requests_the_nearest_frame_without_seeking_or_capturing()
    {
        var timeline = new ReplayTimelineSurface();
        timeline.SetMaximum(10);
        timeline.SetPosition(2);
        var window = Show(timeline);
        try
        {
            var contextFrames = new List<int>();
            var seekCount = 0;
            var capturedOnPress = true;
            var capturedOnRelease = true;
            var releaseWasHandled = true;
            timeline.ContextFrameRequested += (_, e) => contextFrames.Add(e.LogicalIndex);
            timeline.SeekRequested += (_, _) => seekCount++;
            timeline.AddHandler(
                InputElement.PointerPressedEvent,
                (_, e) => capturedOnPress = ReferenceEquals(e.Pointer.Captured, timeline),
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            timeline.AddHandler(
                InputElement.PointerReleasedEvent,
                (_, e) =>
                {
                    capturedOnRelease = ReferenceEquals(e.Pointer.Captured, timeline);
                    releaseWasHandled = e.Handled;
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            var usableWidth = timeline.Bounds.Width - 10;
            var localPoint = new Point(5 + (usableWidth * 0.74), timeline.Bounds.Height / 2);
            var windowPoint = timeline.TranslatePoint(localPoint, window) ??
                throw new InvalidOperationException("Timeline could not translate the test point.");

            window.MouseDown(windowPoint, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(windowPoint, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([7], contextFrames);
            Assert.Equal(0, seekCount);
            Assert.False(capturedOnPress);
            Assert.False(capturedOnRelease);
            Assert.False(releaseWasHandled);

            var keyboardRequest = new ContextRequestedEventArgs
            {
                RoutedEvent = InputElement.ContextRequestedEvent,
            };
            timeline.RaiseEvent(keyboardRequest);

            Assert.Equal([7, 2], contextFrames);
            Assert.Equal(0, seekCount);
            Assert.False(keyboardRequest.Handled);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Left_drag_owns_capture_and_handles_its_release()
    {
        var timeline = new ReplayTimelineSurface();
        timeline.SetMaximum(10);
        var window = Show(timeline);
        try
        {
            var capturedOnPress = false;
            var capturedOnRelease = true;
            var releaseWasHandled = false;
            int? soughtFrame = null;
            timeline.SeekRequested += (_, e) => soughtFrame = e.LogicalIndex;
            timeline.AddHandler(
                InputElement.PointerPressedEvent,
                (_, e) => capturedOnPress = ReferenceEquals(e.Pointer.Captured, timeline),
                RoutingStrategies.Bubble,
                handledEventsToo: true);
            timeline.AddHandler(
                InputElement.PointerReleasedEvent,
                (_, e) =>
                {
                    capturedOnRelease = e.Pointer.Captured is not null;
                    releaseWasHandled = e.Handled;
                },
                RoutingStrategies.Bubble,
                handledEventsToo: true);

            var localPoint = new Point(timeline.Bounds.Width / 2, timeline.Bounds.Height / 2);
            var windowPoint = timeline.TranslatePoint(localPoint, window) ??
                throw new InvalidOperationException("Timeline could not translate the test point.");

            window.MouseDown(windowPoint, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(windowPoint, MouseButton.Left, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.True(capturedOnPress);
            Assert.False(capturedOnRelease);
            Assert.True(releaseWasHandled);
            Assert.Equal(5, soughtFrame);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Output_timeline_exposes_a_full_hit_area_and_independent_range_state()
    {
        var timeline = new OutputVideoTimelineSurface();
        timeline.SetFrameCount(21, 180);
        timeline.SetSourceRange(11, 3, 10);
        timeline.Measure(new Size(500, 100));
        timeline.Arrange(new Rect(0, 0, 500, 100));

        Assert.Equal(3, timeline.RangeStart);
        Assert.Equal(10, timeline.RangeEnd);
        Assert.True(((ICustomHitTest)timeline).HitTest(new Point(250, 50)));
        Assert.False(((ICustomHitTest)timeline).HitTest(new Point(501, 50)));
    }

    private static Window Show(ReplayTimelineSurface timeline)
    {
        var window = new Window
        {
            Width = 500,
            Height = 100,
            Content = timeline,
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
