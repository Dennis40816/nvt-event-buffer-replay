using System.Diagnostics;
using Nvt.Replay.Analysis;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Rendering;

internal sealed record PerformanceBenchmarkReport(
    string SchemaVersion,
    string SourcePath,
    long SourceBytes,
    int PhysicalRecords,
    long MaximumByteOffset,
    int LogicalFrames,
    int Checkpoints,
    TimeSpan TimelineDuration,
    double LoadMilliseconds,
    double DecodeAndIndexMilliseconds,
    double SeekMilliseconds,
    int SeekSamples,
    double RenderMilliseconds,
    int RenderedFrames,
    double RenderFramesPerSecond,
    long PeakWorkingSetBytes,
    string OperatingSystem,
    string Framework,
    int ProcessorCount);

internal static class PerformanceBenchmark
{
    public static async Task<PerformanceBenchmarkReport> RunCommonAsync(
        string path,
        CommonEventBufferVersion version,
        string? adapterId = null,
        int seekSamples = 10_000,
        int renderFrames = 60,
        CancellationToken cancellationToken = default)
    {
        if (seekSamples < 0 || renderFrames < 0) throw new ArgumentOutOfRangeException(nameof(seekSamples));
        var stopwatch = Stopwatch.StartNew();
        var capture = await CaptureSession.LoadAsync(path, cancellationToken: cancellationToken, adapterId: adapterId);
        var loadMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        stopwatch.Restart();
        var decoded = capture.DecodeCommon(version);
        var replay = new CommonReplaySession(decoded.Frames);
        var decodeMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        var actualSeeks = replay.Count == 0 ? 0 : seekSamples;
        stopwatch.Restart();
        for (var sample = 0; sample < actualSeeks; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = (int)((long)sample * 104_729 % replay.Count);
            _ = replay.Seek(index);
        }
        var seekMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        var actualRenders = Math.Min(renderFrames, replay.Count);
        var extent = ReplayExtent.Measure(replay.AllReportedContacts);
        stopwatch.Restart();
        for (var sample = 0; sample < actualRenders; sample++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = actualRenders == 1 ? 0 : sample * (replay.Count - 1) / (actualRenders - 1);
            var scene = ReplaySceneFactory.Create(replay.Seek(index), replay.Count, extent);
            _ = ReplayFrameRenderer.RenderRgb(scene, 640, 360);
        }
        var renderMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
        var process = Process.GetCurrentProcess();
        return new PerformanceBenchmarkReport(
            "1.0",
            Path.GetFullPath(path),
            new FileInfo(path).Length,
            capture.Records.Count,
            capture.Records.Select(record => record.Location.ByteOffset).DefaultIfEmpty().Max(),
            replay.Count,
            replay.CheckpointCount,
            replay.Timeline.LastOrDefault()?.RecordedTime ?? TimeSpan.Zero,
            loadMilliseconds,
            decodeMilliseconds,
            seekMilliseconds,
            actualSeeks,
            renderMilliseconds,
            actualRenders,
            renderMilliseconds <= 0 ? 0 : actualRenders * 1000d / renderMilliseconds,
            process.PeakWorkingSet64,
            Environment.OSVersion.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount);
    }
}
