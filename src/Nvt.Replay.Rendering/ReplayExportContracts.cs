using Nvt.Replay.Analysis;

namespace Nvt.Replay.Rendering;

/// <summary>Selects how the duration of each exported frame is derived.</summary>
public enum ReplayExportClock
{
    /// <summary>Uses the recorded time between consecutive frames, falling back to the replay frame interval.</summary>
    Recorded,
    /// <summary>Uses the fixed replay frame interval for every frame.</summary>
    Frame,
}

/// <summary>Identifies the kind of output an export produces.</summary>
public enum ReplayExportKind
{
    /// <summary>Indicates an MP4 video encoded with ffmpeg.</summary>
    Mp4,
    /// <summary>Indicates a directory of PNG frames written without an encoder.</summary>
    PngSequence,
}

/// <summary>Describes one range export: output location, frame size and rate, speed, clock and render settings.</summary>
/// <param name="OutputPath">The output video file path; a PNG sequence is written to a directory named by appending .frames to this path, extension included.</param>
/// <param name="Range">The inclusive logical index range to export.</param>
/// <param name="Width">The output width in pixels.</param>
/// <param name="Height">The output height in pixels.</param>
/// <param name="FrameRate">The output frame rate in frames per second.</param>
/// <param name="Speed">The playback speed factor applied to the frame durations.</param>
/// <param name="Clock">How the duration of each frame is derived.</param>
/// <param name="Mode">The render mode used for every frame.</param>
/// <param name="FfmpegPath">An explicit ffmpeg executable path, or null to use the default lookup.</param>
/// <param name="SourceFileName">The source file name recorded in the manifest, or null.</param>
/// <param name="SourceSha256">The source SHA-256 recorded in the manifest, or null.</param>
/// <param name="DecodeConfiguration">The decode configuration recorded in the manifest, or null.</param>
/// <param name="RenderSettings">Explicit render settings, or null to use the settings for <paramref name="Mode"/>.</param>
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
    /// <summary>Gets whether a PNG sequence is written when no video encoder is available.</summary>
    public bool AllowPngSequenceFallback { get; init; }
    /// <summary>Gets the requested video encoder name, or null to try the default encoders.</summary>
    public string? VideoEncoder { get; init; }

    /// <summary>Gets the render settings with their mode set to <see cref="Mode"/>.</summary>
    public ReplayRenderSettings ResolvedRenderSettings =>
        (RenderSettings ?? new ReplayRenderSettings(Mode)) with { Mode = Mode };
}

/// <summary>Maps one logical index to the number of consecutive output frames that show it.</summary>
/// <param name="LogicalIndex">The logical replay index shown by the frames.</param>
/// <param name="RepeatCount">The number of consecutive output frames that show it.</param>
public sealed record ReplayFramePlanEntry(int LogicalIndex, int RepeatCount);

/// <summary>Captures the replay and timing inputs that a frame plan was built from.</summary>
/// <param name="ReplayFrameCount">The number of frames of the replay.</param>
/// <param name="ReplayFrameInterval">The replay frame interval.</param>
/// <param name="Range">The inclusive logical index range of the plan.</param>
/// <param name="Clock">How frame durations were derived.</param>
/// <param name="FrameRate">The output frame rate in frames per second.</param>
/// <param name="Speed">The playback speed factor.</param>
public sealed record ReplayFramePlanIdentity(
    int ReplayFrameCount,
    TimeSpan ReplayFrameInterval,
    AnalysisRange Range,
    ReplayExportClock Clock,
    int FrameRate,
    double Speed);

/// <summary>Describes a finished export in the manifest file written next to its output.</summary>
/// <param name="SchemaVersion">The manifest schema version.</param>
/// <param name="SourceFileName">The source file name, or null.</param>
/// <param name="SourceSha256">The source SHA-256, or null.</param>
/// <param name="DecodeConfiguration">The decode configuration used, or null.</param>
/// <param name="Range">The inclusive logical index range exported.</param>
/// <param name="Clock">How frame durations were derived.</param>
/// <param name="Mode">The render mode used.</param>
/// <param name="Width">The output width in pixels.</param>
/// <param name="Height">The output height in pixels.</param>
/// <param name="FrameRate">The output frame rate in frames per second.</param>
/// <param name="Speed">The playback speed factor.</param>
/// <param name="OutputFrameCount">The number of output frames.</param>
/// <param name="Duration">The duration of the output.</param>
/// <param name="Kind">The kind of output.</param>
/// <param name="Encoder">The encoder that produced the output.</param>
/// <param name="EncoderWarning">A warning about the encoder choice, or null.</param>
/// <param name="RenderSettings">The render settings used.</param>
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

/// <summary>Reports the files an export wrote and the size of its output.</summary>
/// <param name="Kind">The kind of output.</param>
/// <param name="OutputPath">The video file or PNG sequence directory.</param>
/// <param name="ManifestPath">The manifest file path.</param>
/// <param name="OutputFrameCount">The number of output frames.</param>
/// <param name="Duration">The duration of the output.</param>
/// <param name="Warning">A warning about the export, or null.</param>
public sealed record ReplayExportResult(
    ReplayExportKind Kind,
    string OutputPath,
    string ManifestPath,
    int OutputFrameCount,
    TimeSpan Duration,
    string? Warning);

/// <summary>Reports export progress as completed frames of a total, with the current stage.</summary>
/// <param name="CompletedFrames">The number of frames completed so far.</param>
/// <param name="TotalFrames">The total number of frames.</param>
/// <param name="Stage">A short name of the current stage.</param>
public sealed record ReplayExportProgress(
    int CompletedFrames,
    int TotalFrames,
    string Stage)
{
    /// <summary>Gets the completed share as a percentage from 0 to 100, or 0 when there are no frames.</summary>
    public double Percent => TotalFrames == 0
        ? 0
        : Math.Clamp(CompletedFrames * 100d / TotalFrames, 0, 100);
}
