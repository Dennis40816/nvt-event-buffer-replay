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

public sealed class ReplayPaintLabelDeterminismTests
{
    [AvaloniaFact]
    public void Final_frame_labels_ignore_whether_intermediate_frame_was_drawn()
    {
        var drawn = CaptureFinalFrame(drawIntermediate: true);
        var skipped = CaptureFinalFrame(drawIntermediate: false);

        Assert.Equal(drawn, skipped);
    }

    private static byte[] CaptureFinalFrame(bool drawIntermediate)
    {
        var surface = new ReplayPaintSurface();
        var window = new Window { Width = 640, Height = 480, Content = surface };
        try
        {
            window.Show();
            surface.Show(Scene(0, 80));
            using (var initial = window.CaptureRenderedFrame())
                Assert.NotNull(initial);

            surface.Show(Scene(1, 440));
            if (drawIntermediate)
            {
                using var intermediate = window.CaptureRenderedFrame();
                Assert.NotNull(intermediate);
            }

            surface.Show(Scene(2, 240));
            using var final = window.CaptureRenderedFrame() ??
                throw new InvalidOperationException("Headless frame capture failed.");
            using var stream = new MemoryStream();
            final.Save(stream, PngBitmapEncoderOptions.Default);
            return stream.ToArray();
        }
        finally
        {
            window.Close();
        }
    }

    private static ReplayScene Scene(int logicalIndex, ushort y) => new(
        logicalIndex,
        3,
        new ReplayTimelineEntry(
            logicalIndex,
            TimeSpan.FromMilliseconds(logicalIndex),
            TimeSpan.FromMilliseconds(logicalIndex),
            TimeSpan.FromMilliseconds(logicalIndex),
            TimeSpan.FromMilliseconds(1),
            false,
            false),
        [],
        [new ReplayContact(1, TouchType.Finger, TouchStatus.Move, 320, y, $"source-{logicalIndex}")],
        false,
        new ReplayExtent(640, 480),
        [],
        [],
        [],
        false,
        false,
        false);
}
