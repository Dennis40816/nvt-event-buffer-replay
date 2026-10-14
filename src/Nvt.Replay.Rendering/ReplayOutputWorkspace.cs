using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

/// <summary>Identifies the content selected for replay output.</summary>
public enum ReplayOutputContentType
{
    /// <summary>Selects an MP4 video of the replay.</summary>
    Mp4Video,
    /// <summary>Selects a heatmap of coordinate sample counts.</summary>
    Heatmap,
    /// <summary>Selects a plot of reported contact points.</summary>
    ReportedPointPlot,
    /// <summary>Selects a replay analysis data package.</summary>
    DataPackage,
}

/// <summary>Holds the selected output content, video timing, dimensions, and logical frame range.</summary>
/// <param name="ContentType">The kind of replay output selected.</param>
/// <param name="Clock">How the duration of each exported frame is derived.</param>
/// <param name="Speed">The playback speed factor applied to frame durations.</param>
/// <param name="FrameRate">The output video frame rate in frames per second.</param>
/// <param name="Width">The output video width in pixels.</param>
/// <param name="Height">The output video height in pixels.</param>
/// <param name="Range">The inclusive logical frame index range selected for output.</param>
public sealed record ReplayOutputSettings(
    ReplayOutputContentType ContentType,
    ReplayExportClock Clock,
    double Speed,
    int FrameRate,
    int Width,
    int Height,
    AnalysisRange Range)
{
    /// <summary>Gets MP4 defaults with the frame clock, normal speed, 120 FPS, 1920 by 1080 pixels, and logical frame zero.</summary>
    public static ReplayOutputSettings Default { get; } = new(
        ReplayOutputContentType.Mp4Video,
        ReplayExportClock.Frame,
        1,
        120,
        1920,
        1080,
        new AnalysisRange(0, 0));
}

/// <summary>Holds validation errors for the selected replay output settings.</summary>
/// <param name="Errors">The validation error messages for the selected settings and replay.</param>
public sealed record ReplayOutputValidation(IReadOnlyList<string> Errors)
{
    /// <summary>Gets whether validation found no errors in the selected output settings and replay.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>Identifies an output plan by its replay identity, frame timing, and output settings.</summary>
/// <param name="ReplayIdentity">The caller-supplied identity of the decoded replay.</param>
/// <param name="ReplayFrameCount">The number of logical frames in the decoded replay.</param>
/// <param name="ReplayFrameInterval">The decoded replay's fixed frame interval.</param>
/// <param name="Settings">The output settings associated with the plan.</param>
public sealed record ReplayOutputPlanIdentity(
    string ReplayIdentity,
    int ReplayFrameCount,
    TimeSpan ReplayFrameInterval,
    ReplayOutputSettings Settings)
{
    /// <summary>Gets the replay and sampling inputs from this plan identity without presentation settings.</summary>
    public ReplayOutputSamplingIdentity SamplingIdentity => new(
        ReplayIdentity,
        ReplayFrameCount,
        ReplayFrameInterval,
        Settings.Clock,
        Settings.Speed,
        Settings.FrameRate,
        Settings.Range);
}

/// <summary>
/// The complete set of inputs that changes replay frame sampling. Presentation-only settings,
/// including output dimensions and Paint rendering options, deliberately do not participate.
/// </summary>
public sealed record ReplayOutputSamplingIdentity(
    string ReplayIdentity,
    int ReplayFrameCount,
    TimeSpan ReplayFrameInterval,
    ReplayExportClock Clock,
    double Speed,
    int FrameRate,
    AnalysisRange Range);

/// <summary>Holds video frame, duration, pixel work, and RGBA byte estimates with a confirmation flag.</summary>
/// <param name="OutputFrameCount">The number of output video frames, including repeated frames.</param>
/// <param name="Duration">The output duration calculated from the frame count and frame rate.</param>
/// <param name="PixelFrameWork">The output frame count multiplied by width and height, saturated at long.MaxValue.</param>
/// <param name="EstimatedRgbaBytes">The pixel frame work multiplied by four bytes, saturated at long.MaxValue.</param>
/// <param name="RequiresConfirmation">Whether the frame count or duration reaches a long export threshold.</param>
public sealed record ReplayOutputEstimate(
    int OutputFrameCount,
    TimeSpan Duration,
    long PixelFrameWork,
    long EstimatedRgbaBytes,
    bool RequiresConfirmation);

/// <summary>Groups the video preview's output identity, frame sampling entries, and resource estimates.</summary>
/// <param name="Identity">The replay and output settings used to build the preview.</param>
/// <param name="Entries">The logical frame indices and repeat counts selected for the preview.</param>
/// <param name="Estimate">The preview's output frame, duration, and resource estimates.</param>
public sealed record ReplayOutputPreviewPlan(
    ReplayOutputPlanIdentity Identity,
    IReadOnlyList<ReplayFramePlanEntry> Entries,
    ReplayOutputEstimate Estimate)
{
    /// <summary>Gets the entries as an immutable frame plan snapshot, throwing if they are not a snapshot.</summary>
    public ReplayFramePlanSnapshot FramePlan => Entries as ReplayFramePlanSnapshot ??
        throw new InvalidOperationException("The preview was not created from an immutable replay frame plan.");
}

/// <summary>
/// Headless output workspace state. Avalonia and CLI can project controls onto this model without
/// owning a second copy of replay sampling or validation rules.
/// </summary>
public sealed class ReplayOutputWorkspace
{
    /// <summary>Specifies the maximum accepted video output width in pixels.</summary>
    public const int MaximumWidth = 7680;
    /// <summary>Specifies the maximum accepted video output height in pixels.</summary>
    public const int MaximumHeight = 4320;
    /// <summary>Specifies the output frame count at which an export requires confirmation.</summary>
    public const int LongExportFrameThreshold = 10_000;
    /// <summary>Specifies the output duration at which an export requires confirmation.</summary>
    public static readonly TimeSpan LongExportDurationThreshold = TimeSpan.FromMinutes(2);

    /// <summary>Creates an output workspace with the supplied settings or the default settings.</summary>
    public ReplayOutputWorkspace(ReplayOutputSettings? settings = null)
    {
        Settings = settings ?? ReplayOutputSettings.Default;
    }

    /// <summary>Gets the currently selected replay output settings.</summary>
    public ReplayOutputSettings Settings { get; private set; }
    /// <summary>Gets the number of updates that changed the workspace settings.</summary>
    public long Revision { get; private set; }

    /// <summary>Replaces changed settings, increments the revision, and returns whether a change occurred.</summary>
    public bool Update(ReplayOutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings == Settings) return false;
        Settings = settings;
        Revision++;
        return true;
    }

    /// <summary>Checks for a nonempty replay, an in-bounds output range, and valid MP4 timing and dimensions.</summary>
    public ReplayOutputValidation Validate(ITouchReplaySession? replay)
    {
        var errors = new List<string>();
        if (replay is null || replay.Count == 0)
        {
            errors.Add("A non-empty decoded replay is required.");
            return new ReplayOutputValidation(errors);
        }

        if (Settings.Range.StartLogicalIndex < 0 ||
            Settings.Range.EndLogicalIndex < Settings.Range.StartLogicalIndex ||
            Settings.Range.EndLogicalIndex >= replay.Count)
            errors.Add("Output range must be within the decoded replay.");

        if (Settings.ContentType == ReplayOutputContentType.Mp4Video)
        {
            if (!double.IsFinite(Settings.Speed) || Settings.Speed is <= 0 or > 100)
                errors.Add("Video speed must be finite, greater than 0, and no more than 100×.");
            if (Settings.FrameRate is < 1 or > ReplayFramePlan.MaximumFrameRate)
                errors.Add($"Video frame rate must be between 1 and {ReplayFramePlan.MaximumFrameRate} FPS.");
            if (Settings.Width is < 320 or > MaximumWidth || Settings.Width % 2 != 0 ||
                Settings.Height is < 180 or > MaximumHeight || Settings.Height % 2 != 0)
                errors.Add($"Video size must use even values: width 320-{MaximumWidth} and height 180-{MaximumHeight}.");
        }
        return new ReplayOutputValidation(errors);
    }

    /// <summary>Validates MP4 settings and builds an immutable frame sampling plan with output estimates.</summary>
    public ReplayOutputPreviewPlan BuildVideoPreview(
        ITouchReplaySession replay,
        string replayIdentity,
        CancellationToken cancellationToken = default,
        IProgress<ReplayExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (string.IsNullOrWhiteSpace(replayIdentity))
            throw new ArgumentException("Replay identity is required.", nameof(replayIdentity));
        if (Settings.ContentType != ReplayOutputContentType.Mp4Video)
            throw new InvalidOperationException("A video preview can only be built for MP4 output.");
        var validation = Validate(replay);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join(' ', validation.Errors), nameof(Settings));

        var options = CreateExportOptions("preview.mp4");
        var entries = ReplayFramePlan.BuildSnapshot(replay, options, cancellationToken, progress);
        return CreateVideoPreview(replay, replayIdentity, entries);
    }

    internal ReplayOutputPreviewPlan CreateVideoPreview(
        ITouchReplaySession replay,
        string replayIdentity,
        ReplayFramePlanSnapshot entries)
    {
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(entries);
        var expectedPlanIdentity = new ReplayFramePlanIdentity(
            replay.Count,
            replay.FrameInterval,
            Settings.Range,
            Settings.Clock,
            Settings.FrameRate,
            Settings.Speed);
        if (!entries.IsFor(replay, expectedPlanIdentity))
            throw new ArgumentException("Replay frame plan is stale for the selected replay sampling settings.", nameof(entries));

        var outputFrameCount = ReplayFramePlan.OutputFrameCount(entries);
        var duration = TimeSpan.FromSeconds(outputFrameCount / (double)Settings.FrameRate);
        var pixelFrameWork = MultiplySaturated(outputFrameCount, Settings.Width, Settings.Height);
        var rgbaBytes = MultiplySaturated(pixelFrameWork, 4);
        var estimate = new ReplayOutputEstimate(
            outputFrameCount,
            duration,
            pixelFrameWork,
            rgbaBytes,
            outputFrameCount >= LongExportFrameThreshold || duration >= LongExportDurationThreshold);
        var identity = new ReplayOutputPlanIdentity(
            replayIdentity,
            replay.Count,
            replay.FrameInterval,
            Settings);
        return new ReplayOutputPreviewPlan(identity, entries, estimate);
    }

    /// <summary>Combines the current output settings with the supplied destination, render options, and source metadata.</summary>
    public ReplayExportOptions CreateExportOptions(
        string outputPath,
        ReplayRenderMode mode = ReplayRenderMode.Compare,
        string? ffmpegPath = null,
        string? sourceFileName = null,
        string? sourceSha256 = null,
        ReplayDecodeConfiguration? decodeConfiguration = null,
        ReplayRenderSettings? renderSettings = null) =>
        new(
            outputPath,
            Settings.Range,
            Settings.Width,
            Settings.Height,
            Settings.FrameRate,
            Settings.Speed,
            Settings.Clock,
            mode,
            ffmpegPath,
            sourceFileName,
            sourceSha256,
            decodeConfiguration,
            renderSettings);

    private static long MultiplySaturated(long left, long right)
    {
        if (left <= 0 || right <= 0) return 0;
        return left > long.MaxValue / right ? long.MaxValue : left * right;
    }

    private static long MultiplySaturated(long first, long second, long third) =>
        MultiplySaturated(MultiplySaturated(first, second), third);
}
