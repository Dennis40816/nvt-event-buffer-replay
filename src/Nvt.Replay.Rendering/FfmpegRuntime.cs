using System.Diagnostics;
using System.Text;

namespace Nvt.Replay.Rendering;

/// <summary>Represents an FFmpeg runtime or encoder availability failure during replay export.</summary>
public sealed class ReplayEncoderUnavailableException : InvalidOperationException
{
    /// <summary>Initializes an encoder availability exception with the specified error message.</summary>
    public ReplayEncoderUnavailableException(string message) : base(message) { }
    /// <summary>Initializes an encoder availability exception with an error message and the underlying exception.</summary>
    public ReplayEncoderUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Stores the FFmpeg executable path, available reviewed H.264 encoders, and runtime identity.</summary>
/// <param name="ExecutablePath">The path to the FFmpeg executable used by the probe.</param>
/// <param name="H264Encoders">The available reviewed H.264 encoder names in preference order.</param>
/// <param name="Identity">The executable file name used to identify the probed runtime.</param>
public sealed record FfmpegProbeResult(
    string ExecutablePath,
    IReadOnlyList<string> H264Encoders,
    string Identity)
{
    /// <summary>Gets the first available reviewed H.264 encoder in preference order.</summary>
    public string PreferredH264Encoder => H264Encoders[0];
}

/// <summary>
/// Resolves and validates the command-line FFmpeg boundary used by MP4 export.
/// The desktop release places its reviewed runtime under tools/ffmpeg/bin.
/// </summary>
public static class FfmpegRuntime
{
    /// <summary>Names the environment variable used to locate the FFmpeg executable.</summary>
    public const string EnvironmentVariable = "NVT_REPLAY_FFMPEG";

    /// <summary>Finds an existing FFmpeg executable from an explicit path, environment settings, bundled files, or PATH.</summary>
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

    /// <summary>Asynchronously probes FFmpeg for available reviewed H.264 encoders and runtime identity.</summary>
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
