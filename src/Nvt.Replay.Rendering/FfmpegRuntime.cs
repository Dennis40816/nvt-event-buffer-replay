using System.Diagnostics;
using System.Text;

namespace Nvt.Replay.Rendering;

public sealed class ReplayEncoderUnavailableException : InvalidOperationException
{
    public ReplayEncoderUnavailableException(string message) : base(message) { }
    public ReplayEncoderUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed record FfmpegProbeResult(
    string ExecutablePath,
    IReadOnlyList<string> H264Encoders,
    string Identity)
{
    public string PreferredH264Encoder => H264Encoders[0];
}

/// <summary>
/// Resolves and validates the command-line FFmpeg boundary used by MP4 export.
/// The desktop release places its reviewed runtime under tools/ffmpeg/bin.
/// </summary>
public static class FfmpegRuntime
{
    public const string EnvironmentVariable = "NVT_REPLAY_FFMPEG";

    public static string? ResolveExecutable(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        var environmentPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentPath) && File.Exists(environmentPath))
            return Path.GetFullPath(environmentPath);

        var executable = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var relativePath in new[]
                 {
                     Path.Combine("tools", "ffmpeg", "bin", executable),
                     Path.Combine("tools", "ffmpeg", executable),
                 })
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, relativePath);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), executable);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    public static async Task<FfmpegProbeResult> ProbeAsync(
        string? explicitPath = null,
        CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable(explicitPath);
        if (executable is null)
            throw new ReplayEncoderUnavailableException(
                "The reviewed FFmpeg runtime is missing. Reinstall the release package or provide a valid FFmpeg executable; no PNG fallback was created.");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-encoders");

        try
        {
            if (!process.Start())
                throw new ReplayEncoderUnavailableException("FFmpeg did not start; no output was created.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var combined = (await stdout.ConfigureAwait(false)) + Environment.NewLine +
                           (await stderr.ConfigureAwait(false));
            if (process.ExitCode != 0)
                throw new ReplayEncoderUnavailableException(
                    $"FFmpeg capability probe failed with code {process.ExitCode}: {TrimProbeMessage(combined)}");

            var encoders = PreferredEncoders()
                .Where(name => ContainsEncoder(combined, name))
                .ToArray();
            if (encoders.Length == 0)
                throw new ReplayEncoderUnavailableException(
                    "FFmpeg is present but exposes no reviewed H.264 encoder (h264_mf, libopenh264, or libx264); no output was created.");
            return new FfmpegProbeResult(executable, encoders, Path.GetFileName(executable));
        }
        catch (OperationCanceledException)
        {
            TryStop(process);
            throw;
        }
        catch (ReplayEncoderUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new ReplayEncoderUnavailableException(
                $"FFmpeg capability probe failed: {exception.Message}. No output was created.",
                exception);
        }
    }

    private static IEnumerable<string> PreferredEncoders()
    {
        if (OperatingSystem.IsWindows()) yield return "h264_mf";
        yield return "libopenh264";
        yield return "libx264";
    }

    private static bool ContainsEncoder(string output, string encoder)
    {
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length >= 2 && string.Equals(fields[1], encoder, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string TrimProbeMessage(string value)
    {
        var singleLine = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 240 ? singleLine : singleLine[..240] + "…";
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }
}
