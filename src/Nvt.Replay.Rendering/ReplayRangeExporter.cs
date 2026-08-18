using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public enum ReplayExportClock
{
    Recorded,
    Frame,
}

public enum ReplayExportKind
{
    Mp4,
    PngSequence,
}

public sealed record ReplayExportOptions(
    string OutputPath,
    AnalysisRange Range,
    int Width = 1280,
    int Height = 720,
    int FrameRate = 30,
    double Speed = 1,
    ReplayExportClock Clock = ReplayExportClock.Recorded,
    ReplayRenderMode Mode = ReplayRenderMode.Compare,
    string? FfmpegPath = null,
    string? SourceFileName = null,
    string? SourceSha256 = null,
    ReplayDecodeConfiguration? DecodeConfiguration = null,
    ReplayRenderSettings? RenderSettings = null)
{
    public ReplayRenderSettings ResolvedRenderSettings =>
        (RenderSettings ?? new ReplayRenderSettings(Mode)) with { Mode = Mode };
}

public sealed record ReplayFramePlanEntry(int LogicalIndex, int RepeatCount);

public sealed record ReplayVideoManifest(
    int SchemaVersion,
    string? SourceFileName,
    string? SourceSha256,
    ReplayDecodeConfiguration? DecodeConfiguration,
    AnalysisRange Range,
    ReplayExportClock Clock,
    ReplayRenderMode Mode,
    int Width,
    int Height,
    int FrameRate,
    double Speed,
    int OutputFrameCount,
    TimeSpan Duration,
    ReplayExportKind Kind,
    string Encoder,
    string? EncoderWarning,
    ReplayRenderSettings RenderSettings);

public sealed record ReplayExportResult(
    ReplayExportKind Kind,
    string OutputPath,
    string ManifestPath,
    int OutputFrameCount,
    TimeSpan Duration,
    string? Warning);

public sealed record ReplayExportProgress(
    int CompletedFrames,
    int TotalFrames,
    string Stage)
{
    public double Percent => TotalFrames == 0
        ? 0
        : Math.Clamp(CompletedFrames * 100d / TotalFrames, 0, 100);
}

public static class ReplayFramePlan
{
    public static IReadOnlyList<ReplayFramePlanEntry> Build(ITouchReplaySession replay, ReplayExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(replay);
        Validate(replay, options);
        var scaledDurations = new List<double>();
        for (var index = options.Range.StartLogicalIndex; index <= options.Range.EndLogicalIndex; index++)
        {
            var duration = options.Clock == ReplayExportClock.Frame
                ? replay.FrameInterval
                : index < options.Range.EndLogicalIndex
                    ? replay.Timeline[index + 1].RecordedTime - replay.Timeline[index].RecordedTime
                    : replay.FrameInterval;
            if (duration <= TimeSpan.Zero) duration = replay.FrameInterval;
            scaledDurations.Add(duration.TotalSeconds / options.Speed);
        }

        var outputFrameCount = Math.Max(1, (int)Math.Round(
            scaledDurations.Sum() * options.FrameRate,
            MidpointRounding.AwayFromZero));
        var result = new List<ReplayFramePlanEntry>();
        var sourceOffset = 0;
        var sourceEndTime = scaledDurations[0];
        for (var outputIndex = 0; outputIndex < outputFrameCount; outputIndex++)
        {
            var sampleTime = outputIndex / (double)options.FrameRate;
            while (sourceOffset < scaledDurations.Count - 1 && sampleTime >= sourceEndTime)
            {
                sourceOffset++;
                sourceEndTime += scaledDurations[sourceOffset];
            }

            // Preserve both boundaries when the output has enough frames. This does not
            // invent coordinates; it chooses the last captured frame for the last sample.
            if (outputFrameCount > 1 && outputIndex == outputFrameCount - 1)
                sourceOffset = scaledDurations.Count - 1;

            var logicalIndex = options.Range.StartLogicalIndex + sourceOffset;
            if (result.Count > 0 && result[^1].LogicalIndex == logicalIndex)
                result[^1] = result[^1] with { RepeatCount = result[^1].RepeatCount + 1 };
            else
                result.Add(new ReplayFramePlanEntry(logicalIndex, 1));
        }
        return result;
    }

    public static int OutputFrameCount(IReadOnlyList<ReplayFramePlanEntry> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Sum(item => item.RepeatCount);
    }

    public static int LogicalIndexAt(IReadOnlyList<ReplayFramePlanEntry> plan, int outputFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (outputFrameIndex < 0 || outputFrameIndex >= OutputFrameCount(plan))
            throw new ArgumentOutOfRangeException(nameof(outputFrameIndex));
        var remaining = outputFrameIndex;
        foreach (var entry in plan)
        {
            if (remaining < entry.RepeatCount) return entry.LogicalIndex;
            remaining -= entry.RepeatCount;
        }
        throw new InvalidOperationException("Replay frame plan contains no sample for the requested output frame.");
    }

    private static void Validate(ITouchReplaySession replay, ReplayExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (replay.Count == 0 || options.Range.StartLogicalIndex < 0 || options.Range.EndLogicalIndex < options.Range.StartLogicalIndex || options.Range.EndLogicalIndex >= replay.Count)
            throw new ArgumentOutOfRangeException(nameof(options), "Export range must be within a non-empty replay.");
        if (options.Width < 320 || options.Height < 180 || options.Width % 2 != 0 || options.Height % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Export dimensions must be even and at least 320x180.");
        if (options.FrameRate is < 1 or > 240 || options.Speed is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options), "Frame rate must be 1-240 and speed must be >0 and <=100.");
    }
}

public sealed class ReplayRangeExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<ReplayExportResult> ExportAsync(
        ITouchReplaySession replay,
        Func<int, ReplayScene> sceneFactory,
        ReplayExportOptions options,
        CancellationToken cancellationToken = default,
        IProgress<ReplayExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(sceneFactory);
        var plan = ReplayFramePlan.Build(replay, options);
        var totalFrames = ReplayFramePlan.OutputFrameCount(plan);
        progress?.Report(new ReplayExportProgress(0, totalFrames, "Preparing encoder"));
        var fullOutputPath = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath) ?? throw new InvalidOperationException("Output path has no parent directory.");
        Directory.CreateDirectory(outputDirectory);
        if (File.Exists(fullOutputPath)) throw new IOException($"Output already exists and will not be overwritten: {fullOutputPath}");

        var encoder = ResolveFfmpeg(options.FfmpegPath);
        string? warning = null;
        if (encoder is not null)
        {
            var manifestPath = fullOutputPath + ".json";
            if (File.Exists(manifestPath)) throw new IOException($"Output manifest already exists and will not be overwritten: {manifestPath}");
            try
            {
                return await EncodeMp4Async(replay, sceneFactory, options, plan, encoder, fullOutputPath, manifestPath, cancellationToken, progress);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                warning = $"FFmpeg was unavailable or incompatible ({exception.Message}); exported PNG sequence instead.";
            }
        }
        else
        {
            warning = "No reviewed FFmpeg executable was found; exported PNG sequence instead.";
        }
        return await ExportPngSequenceAsync(replay, sceneFactory, options, plan, fullOutputPath + ".frames", warning, cancellationToken, progress);
    }

    private static async Task<ReplayExportResult> EncodeMp4Async(
        ITouchReplaySession replay,
        Func<int, ReplayScene> sceneFactory,
        ReplayExportOptions options,
        IReadOnlyList<ReplayFramePlanEntry> plan,
        string encoder,
        string outputPath,
        string manifestPath,
        CancellationToken cancellationToken,
        IProgress<ReplayExportProgress>? progress)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp.mp4");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = encoder,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        var arguments = new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "rgb24",
            "-video_size", $"{options.Width}x{options.Height}", "-framerate", options.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "pipe:0", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-y", temporaryPath,
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        var started = false;
        var outputCreated = false;
        try
        {
            if (!process.Start()) throw new InvalidOperationException("FFmpeg did not start.");
            started = true;
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            var totalFrames = ReplayFramePlan.OutputFrameCount(plan);
            var completedFrames = 0;
            var progressStep = Math.Max(1, totalFrames / 200);
            progress?.Report(new ReplayExportProgress(0, totalFrames, "Encoding MP4"));
            foreach (var entry in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rgb = ReplayFrameRenderer.RenderRgb(sceneFactory(entry.LogicalIndex), options.Width, options.Height, options.ResolvedRenderSettings);
                for (var repeat = 0; repeat < entry.RepeatCount; repeat++)
                {
                    await process.StandardInput.BaseStream.WriteAsync(rgb, cancellationToken);
                    completedFrames++;
                    if (completedFrames == totalFrames || completedFrames % progressStep == 0)
                        progress?.Report(new ReplayExportProgress(completedFrames, totalFrames, "Encoding MP4"));
                }
            }
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
            process.StandardInput.Close();
            progress?.Report(new ReplayExportProgress(totalFrames, totalFrames, "Finalizing MP4"));
            await process.WaitForExitAsync(cancellationToken);
            var error = await stderr;
            if (process.ExitCode != 0 || !File.Exists(temporaryPath))
                throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}: {error.Trim()}");
            File.Move(temporaryPath, outputPath);
            outputCreated = true;
            var manifest = Manifest(options, plan, ReplayExportKind.Mp4, Path.GetFileName(encoder), null);
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);
            return Result(ReplayExportKind.Mp4, outputPath, manifestPath, plan, options.FrameRate, null);
        }
        catch
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (outputCreated && File.Exists(outputPath)) File.Delete(outputPath);
            throw;
        }
    }

    private static async Task<ReplayExportResult> ExportPngSequenceAsync(
        ITouchReplaySession replay,
        Func<int, ReplayScene> sceneFactory,
        ReplayExportOptions options,
        IReadOnlyList<ReplayFramePlanEntry> plan,
        string outputDirectory,
        string warning,
        CancellationToken cancellationToken,
        IProgress<ReplayExportProgress>? progress)
    {
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new IOException($"Fallback output already exists and will not be overwritten: {outputDirectory}");
        var parent = Path.GetDirectoryName(outputDirectory)!;
        var temporaryDirectory = Path.Combine(parent, $".{Path.GetFileName(outputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);
        var outputIndex = 0;
        var totalFrames = ReplayFramePlan.OutputFrameCount(plan);
        var progressStep = Math.Max(1, totalFrames / 200);
        progress?.Report(new ReplayExportProgress(0, totalFrames, "Writing PNG fallback"));
        try
        {
            foreach (var entry in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rgb = ReplayFrameRenderer.RenderRgb(sceneFactory(entry.LogicalIndex), options.Width, options.Height, options.ResolvedRenderSettings);
                for (var repeat = 0; repeat < entry.RepeatCount; repeat++)
                {
                    var path = Path.Combine(temporaryDirectory, $"frame-{outputIndex++:D8}.png");
                    await AtomicOutput.WriteAsync(path, (stream, token) => DeterministicPng.WriteRgbAsync(stream, rgb, options.Width, options.Height, token), cancellationToken);
                    if (outputIndex == totalFrames || outputIndex % progressStep == 0)
                        progress?.Report(new ReplayExportProgress(outputIndex, totalFrames, "Writing PNG fallback"));
                }
            }
            var temporaryManifest = Path.Combine(temporaryDirectory, "export-manifest.json");
            await WriteManifestAsync(temporaryManifest, Manifest(options, plan, ReplayExportKind.PngSequence, "deterministic-png", warning), cancellationToken);
            Directory.Move(temporaryDirectory, outputDirectory);
            return Result(ReplayExportKind.PngSequence, outputDirectory, Path.Combine(outputDirectory, "export-manifest.json"), plan, options.FrameRate, warning);
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            throw;
        }
    }

    private static ReplayVideoManifest Manifest(
        ReplayExportOptions options,
        IReadOnlyList<ReplayFramePlanEntry> plan,
        ReplayExportKind kind,
        string encoder,
        string? warning)
    {
        var count = ReplayFramePlan.OutputFrameCount(plan);
        return new ReplayVideoManifest(
            2, options.SourceFileName, options.SourceSha256, options.DecodeConfiguration, options.Range,
            options.Clock, options.Mode, options.Width, options.Height, options.FrameRate, options.Speed,
            count, TimeSpan.FromSeconds((double)count / options.FrameRate), kind, encoder, warning,
            options.ResolvedRenderSettings);
    }

    private static ReplayExportResult Result(
        ReplayExportKind kind,
        string outputPath,
        string manifestPath,
        IReadOnlyList<ReplayFramePlanEntry> plan,
        int frameRate,
        string? warning)
    {
        var count = ReplayFramePlan.OutputFrameCount(plan);
        return new ReplayExportResult(kind, outputPath, manifestPath, count, TimeSpan.FromSeconds((double)count / frameRate), warning);
    }

    private static Task WriteManifestAsync(string path, ReplayVideoManifest manifest, CancellationToken cancellationToken) =>
        AtomicOutput.WriteAsync(path, (stream, token) => JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, token), cancellationToken);

    private static string? ResolveFfmpeg(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath)) return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;
        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executable);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
