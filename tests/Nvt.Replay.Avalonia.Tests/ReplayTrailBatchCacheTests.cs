using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class ReplayTrailBatchCacheTests
{
    [Fact]
    public void Hundred_thousand_point_frame_switches_reuse_full_chunks_and_only_rebuild_the_tail()
    {
        const int reportCount = 100_003;
        const int switches = 250;
        var points = Enumerable.Range(0, reportCount)
            .Select(index => new ReplayTrailPoint(
                (ushort)(index % 4096),
                (ushort)((index * 7) % 2048),
                index == 0 ? TouchStatus.Enter : TouchStatus.Move,
                index))
            .ToArray();
        var cache = new ReplayTrailBatchCache();
        var complete = cache.Build(Scene(
            new ReplayContactTrail(1, TouchType.Finger, points, IsComplete: true),
            reportCount - 1));

        Assert.Equal(reportCount, complete.Sum(batch => batch.ReportPoints.Length));
        Assert.Equal(49, complete.Count);
        Assert.Equal(reportCount, cache.CoordinateBuildPointCount);
        var cachedEntries = cache.Count;
        var pointsBuiltBeforeSwitching = cache.CoordinateBuildPointCount;

        for (var sample = 0; sample < switches; sample++)
        {
            var visibleCount = 2 + (int)((long)sample * 104_729 % (reportCount - 2));
            var batches = cache.Build(Scene(
                new ReplayContactTrail(
                    1,
                    TouchType.Finger,
                    new ArraySegment<ReplayTrailPoint>(points, 0, visibleCount)),
                visibleCount - 1));
            Assert.Equal(visibleCount, batches.Sum(batch => batch.ReportPoints.Length));
        }

        Assert.Equal(cachedEntries, cache.Count);
        Assert.InRange(
            cache.CoordinateBuildPointCount - pointsBuiltBeforeSwitching,
            0,
            switches * (ReplayTrailChunker.DefaultChunkSize - 1L));
    }

    [AvaloniaFact]
    public void Hundred_thousand_points_render_as_exact_batched_lines_and_markers()
    {
        const int reportCount = 100_003;
        var points = Enumerable.Range(0, reportCount)
            .Select(index => new ReplayTrailPoint(
                (ushort)(index % 4096),
                (ushort)((index * 7) % 2048),
                index == 0 ? TouchStatus.Enter : TouchStatus.Move,
                index))
            .ToArray();
        var surface = new ReplayPaintSurface();
        var window = new Window { Width = 640, Height = 360, Content = surface };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            surface.Show(Scene(
                new ReplayContactTrail(1, TouchType.Finger, points, IsComplete: true),
                reportCount - 1));
            window.Show();
            using var frame = window.CaptureRenderedFrame();

            Assert.NotNull(frame);
            Assert.Equal(49, surface.CachedTrailBatchCount);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"100k render took {stopwatch.Elapsed}.");
        }
        finally
        {
            window.Close();
        }
    }

    private static ReplayScene Scene(ReplayContactTrail trail, int logicalIndex) => new(
        logicalIndex,
        100_003,
        new ReplayTimelineEntry(
            logicalIndex,
            TimeSpan.FromMilliseconds(logicalIndex),
            TimeSpan.FromMilliseconds(logicalIndex),
            TimeSpan.FromMilliseconds(logicalIndex),
            TimeSpan.FromMilliseconds(1),
            false,
            false),
        [],
        [],
        false,
        new ReplayExtent(4096, 2048),
        [trail],
        [],
        [],
        false,
        false,
        false);
}
