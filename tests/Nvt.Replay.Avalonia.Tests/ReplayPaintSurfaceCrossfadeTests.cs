using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class ReplayPaintSurfaceCrossfadeTests
{
    [AvaloniaFact]
    public void CrossfadeRendersADifferentFrameAtStartAndTheFinalFrameAfterDuration()
    {
        var now = Milliseconds(1000);
        using var host = new Host(() => now);
        using var reference = new Host(() => now);
        var next = Scene(2, 380);
        reference.Surface.Show(next);
        var final = reference.Frame();
        host.Surface.Show(Scene(1, 100));
        host.Frame();

        host.Surface.Show(next, crossfade: true);
        var start = host.Frame();
        now += Milliseconds(141);
        var completed = host.Frame();

        Assert.NotEqual(final, start);
        Assert.Equal(final, completed);
    }

    [AvaloniaFact]
    public void ShowWithoutCrossfadeDropsTheOutgoingSceneAndTrails()
    {
        var now = Milliseconds(1000);
        using var host = new Host(() => now);
        using var reference = new Host(() => now);
        var next = Scene(2, 380);
        reference.Surface.Show(next);
        var final = reference.Frame();
        host.Surface.Show(Scene(1, 100));
        host.Surface.Show(next, crossfade: true);
        var fading = host.Frame();

        host.Surface.Show(next, crossfade: false);
        var immediate = host.Frame();

        Assert.NotEqual(final, fading);
        Assert.Equal(final, immediate);
    }

    [AvaloniaFact]
    public void ClearDropsTheOutgoingSceneAndTrailsAndDoesNotReuseThem()
    {
        var now = Milliseconds(1000);
        using var host = new Host(() => now);
        using var reference = new Host(() => now);
        var empty = reference.Frame();
        var next = Scene(2, 380);
        reference.Surface.Show(next);
        var final = reference.Frame();
        host.Surface.Show(Scene(1, 100));
        host.Surface.Show(next, crossfade: true);
        host.Frame();

        host.Surface.Clear();
        var cleared = host.Frame();
        host.Surface.Show(next, crossfade: true);
        var restarted = host.Frame();

        Assert.Equal(empty, cleared);
        Assert.Equal(final, restarted);
    }

    [AvaloniaFact]
    public void ShowingTheFirstSceneWithCrossfadeRendersTheFullScene()
    {
        var now = Milliseconds(1000);
        using var host = new Host(() => now);
        using var reference = new Host(() => now);
        var scene = Scene(1, 100);
        reference.Surface.Show(scene);
        var expected = reference.Frame();

        host.Surface.Show(scene, crossfade: true);

        Assert.Equal(expected, host.Frame());
    }

    private static long Milliseconds(int value) => value * TimeProvider.System.TimestampFrequency / 1000;

    private static ReplayScene Scene(byte id, ushort y) => new(
        id - 1,
        2,
        new ReplayTimelineEntry(id - 1, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(1), false, false),
        [],
        [new ReplayContact(id, TouchType.Finger, TouchStatus.Move, 320, y, $"source-{id}")],
        false,
        new ReplayExtent(640, 480),
        [new ReplayContactTrail(id, TouchType.Finger,
            [new ReplayTrailPoint(100, y, TouchStatus.Enter, 0), new ReplayTrailPoint(320, y, TouchStatus.Move, 1)])],
        [],
        [],
        false,
        false,
        false);

    private sealed class Host : IDisposable
    {
        internal Host(Func<long> readTimestamp)
        {
            Surface = new ReplayPaintSurface(readTimestamp);
            Window = new Window { Width = 640, Height = 480, Content = Surface };
            Window.Show();
        }

        internal ReplayPaintSurface Surface { get; }

        private Window Window { get; }

        internal byte[] Frame()
        {
            Surface.InvalidateVisual();
            using var frame = Window.CaptureRenderedFrame() ??
                throw new InvalidOperationException("Headless frame capture failed.");
            using var stream = new MemoryStream();
            frame.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }

        public void Dispose()
        {
            Surface.Clear();
            Window.Close();
        }
    }
}
