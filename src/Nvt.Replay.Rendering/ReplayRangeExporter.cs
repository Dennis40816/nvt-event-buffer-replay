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
    public const int MaximumFrameRate = 240;
    public const long MaximumOutputFrames = 100_000_000;

    public static IReadOnlyList<ReplayFramePlanEntry> Build(ITouchReplaySession replay, ReplayExportOptions options) =>
        Build(replay, options, CancellationToken.None);

    public static IReadOnlyList<ReplayFramePlanEntry> Build(
        ITouchReplaySession replay,
        ReplayExportOptions options,
        CancellationToken cancellationToken,
        IProgress<ReplayExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        Validate(replay, options);
        var logicalFrameCount = checked(options.Range.EndLogicalIndex - options.Range.StartLogicalIndex + 1);
        var progressStep = Math.Max(1, logicalFrameCount / 200);
        progress?.Report(new ReplayExportProgress(0, logicalFrameCount, "Planning duration"));
        var totalSeconds = 0d;
        for (var index = options.Range.StartLogicalIndex; index <= options.Range.EndLogicalIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalSeconds = AddDurationChecked(totalSeconds, ScaledDurationSeconds(replay, options, index));
            var completed = index - options.Range.StartLogicalIndex + 1;
            if (completed == logicalFrameCount || completed % progressStep == 0)
                progress?.Report(new ReplayExportProgress(completed, logicalFrameCount, "Planning duration"));
        }

        var outputFrameCount = CalculateOutputFrameCount(totalSeconds, options.FrameRate);
        var entries = new List<ReplayFramePlanEntry>(Math.Min(logicalFrameCount, checked((int)outputFrameCount)));
        var cumulativeOutputEnds = new List<long>(entries.Capacity);
        var outputCursor = 0L;
        var cumulativeSeconds = 0d;
        progress?.Report(new ReplayExportProgress(0, logicalFrameCount, "Planning frame map"));
        for (var index = options.Range.StartLogicalIndex; index <= options.Range.EndLogicalIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            cumulativeSeconds = AddDurationChecked(cumulativeSeconds, ScaledDurationSeconds(replay, options, index));
            long outputEnd;
            if (index == options.Range.EndLogicalIndex)
            {
                outputEnd = outputFrameCount;
            }
            else
            {
                outputEnd = FirstOutputIndexAtOrAfter(
                    cumulativeSeconds,
                    options.FrameRate,
                    outputCursor,
                    outputFrameCount);
                // Preserve the final source boundary whenever the output has enough frames,
                // matching the previous sampler's last-frame rule without materializing it.
                if (outputFrameCount > 1) outputEnd = Math.Min(outputEnd, outputFrameCount - 1);
            }

            var repeatCount = outputEnd - outputCursor;
            if (repeatCount > 0)
            {
                entries.Add(new ReplayFramePlanEntry(index, checked((int)repeatCount)));
                cumulativeOutputEnds.Add(outputEnd);
            }
            outputCursor = outputEnd;
            var completed = index - options.Range.StartLogicalIndex + 1;
            if (completed == logicalFrameCount || completed % progressStep == 0)
                progress?.Report(new ReplayExportProgress(completed, logicalFrameCount, "Planning frame map"));
        }

        return new IndexedEntries(entries.ToArray(), cumulativeOutputEnds.ToArray(), outputFrameCount);
    }

    public static int OutputFrameCount(IReadOnlyList<ReplayFramePlanEntry> plan)
    {
        var count = OutputFrameCount64(plan);
        if (count > int.MaxValue)
            throw new InvalidOperationException($"Replay frame plan contains {count:N0} output frames, which exceeds the supported 32-bit preview index.");
        return (int)count;
    }

    public static long OutputFrameCount64(IReadOnlyList<ReplayFramePlanEntry> plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan is IndexedEntries indexed) return indexed.OutputFrameCount;
        var total = 0L;
        foreach (var entry in plan)
        {
            if (entry.RepeatCount < 0)
                throw new ArgumentException("Replay frame plan repeat counts cannot be negative.", nameof(plan));
            total = checked(total + entry.RepeatCount);
            EnsureWithinHardLimit(total);
        }
        return total;
    }

    public static int LogicalIndexAt(IReadOnlyList<ReplayFramePlanEntry> plan, int outputFrameIndex) =>
        LogicalIndexAt(plan, (long)outputFrameIndex);

    public static int LogicalIndexAt(IReadOnlyList<ReplayFramePlanEntry> plan, long outputFrameIndex)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var outputFrameCount = OutputFrameCount64(plan);
        if (outputFrameIndex < 0 || outputFrameIndex >= outputFrameCount)
            throw new ArgumentOutOfRangeException(nameof(outputFrameIndex));

        if (plan is IndexedEntries indexed) return indexed.LogicalIndexAt(outputFrameIndex);
        var cumulativeEnds = new long[plan.Count];
        var cumulative = 0L;
        for (var index = 0; index < plan.Count; index++)
        {
            cumulative = checked(cumulative + plan[index].RepeatCount);
            cumulativeEnds[index] = cumulative;
        }
        return plan[FindCumulativeEntry(cumulativeEnds, outputFrameIndex)].LogicalIndex;
    }

    private static void Validate(ITouchReplaySession replay, ReplayExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (replay.Count == 0 || options.Range.StartLogicalIndex < 0 || options.Range.EndLogicalIndex < options.Range.StartLogicalIndex || options.Range.EndLogicalIndex >= replay.Count)
            throw new ArgumentOutOfRangeException(nameof(options), "Export range must be within a non-empty replay.");
        if (options.Width < 320 || options.Height < 180 || options.Width % 2 != 0 || options.Height % 2 != 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Export dimensions must be even and at least 320x180.");
        if (options.FrameRate is < 1 or > MaximumFrameRate || options.Speed is <= 0 or > 100 || !double.IsFinite(options.Speed))
            throw new ArgumentOutOfRangeException(nameof(options), $"Frame rate must be 1-{MaximumFrameRate} and speed must be finite, >0, and <=100.");
        if (replay.FrameInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(replay), "Replay frame interval must be positive.");
    }

    private static double ScaledDurationSeconds(ITouchReplaySession replay, ReplayExportOptions options, int index)
    {
        TimeSpan duration;
        try
        {
            duration = options.Clock == ReplayExportClock.Frame
                ? replay.FrameInterval
                : index < options.Range.EndLogicalIndex
                    ? replay.Timeline[index + 1].RecordedTime - replay.Timeline[index].RecordedTime
                    : replay.FrameInterval;
        }
        catch (OverflowException exception)
        {
            throw new InvalidOperationException("Recorded replay duration overflowed while planning the export.", exception);
        }
        if (duration <= TimeSpan.Zero) duration = replay.FrameInterval;
        var seconds = duration.TotalSeconds / options.Speed;
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidOperationException("Replay duration is not finite at the selected export speed.");
        return seconds;
    }

    private static double AddDurationChecked(double total, double duration)
    {
        var result = total + duration;
        if (!double.IsFinite(result))
            throw new InvalidOperationException("Replay duration overflowed while planning the export.");
        return result;
    }

    private static long CalculateOutputFrameCount(double totalSeconds, int frameRate)
    {
        var unrounded = totalSeconds * frameRate;
        if (!double.IsFinite(unrounded))
            throw new InvalidOperationException("Output frame count overflowed while planning the export.");
        var rounded = Math.Max(1d, Math.Round(unrounded, MidpointRounding.AwayFromZero));
        if (rounded > long.MaxValue)
            throw new InvalidOperationException("Output frame count exceeds the supported 64-bit range.");
        var count = checked((long)rounded);
        EnsureWithinHardLimit(count);
        return count;
    }

    private static void EnsureWithinHardLimit(long count)
    {
        if (count > MaximumOutputFrames)
            throw new InvalidOperationException(
                $"Export would require {count:N0} frames, exceeding the safety limit of {MaximumOutputFrames:N0}. Reduce the range, frame rate, or Recorded-time gaps, or increase speed.");
    }

    private static long FirstOutputIndexAtOrAfter(
        double sourceEndSeconds,
        int frameRate,
        long minimum,
        long outputFrameCount)
    {
        var low = minimum;
        var high = outputFrameCount;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (middle / (double)frameRate >= sourceEndSeconds) high = middle;
            else low = middle + 1;
        }
        return low;
    }

    private static int FindCumulativeEntry(IReadOnlyList<long> cumulativeEnds, long outputFrameIndex)
    {
        var target = checked(outputFrameIndex + 1);
        var low = 0;
        var high = cumulativeEnds.Count - 1;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (cumulativeEnds[middle] >= target) high = middle;
            else low = middle + 1;
        }
        return low;
    }

    private sealed class IndexedEntries(
        ReplayFramePlanEntry[] entries,
        long[] cumulativeOutputEnds,
        long outputFrameCount) : IReadOnlyList<ReplayFramePlanEntry>
    {
        public long OutputFrameCount { get; } = outputFrameCount;
        public int Count => entries.Length;
        public ReplayFramePlanEntry this[int index] => entries[index];

        public int LogicalIndexAt(long outputFrameIndex) =>
            entries[FindCumulativeEntry(cumulativeOutputEnds, outputFrameIndex)].LogicalIndex;

        public IEnumerator<ReplayFramePlanEntry> GetEnumerator() =>
            ((IEnumerable<ReplayFramePlanEntry>)entries).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => entries.GetEnumerator();
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
        var plan = ReplayFramePlan.Build(replay, options, cancellationToken, progress);
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
