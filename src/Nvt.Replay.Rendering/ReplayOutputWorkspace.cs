using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public enum ReplayOutputContentType
{
    Mp4Video,
    Heatmap,
    ReportedPointPlot,
    DataPackage,
}

public sealed record ReplayOutputSettings(
    ReplayOutputContentType ContentType,
    ReplayExportClock Clock,
    double Speed,
    int FrameRate,
    int Width,
    int Height,
    AnalysisRange Range)
{
    public static ReplayOutputSettings Default { get; } = new(
        ReplayOutputContentType.Mp4Video,
        ReplayExportClock.Frame,
        1,
        120,
        1920,
        1080,
        new AnalysisRange(0, 0));
}

public sealed record ReplayOutputValidation(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record ReplayOutputPlanIdentity(
    string ReplayIdentity,
    int ReplayFrameCount,
    TimeSpan ReplayFrameInterval,
    ReplayOutputSettings Settings)
{
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

public sealed record ReplayOutputEstimate(
    int OutputFrameCount,
    TimeSpan Duration,
    long PixelFrameWork,
    long EstimatedRgbaBytes,
    bool RequiresConfirmation);

public sealed record ReplayOutputPreviewPlan(
    ReplayOutputPlanIdentity Identity,
    IReadOnlyList<ReplayFramePlanEntry> Entries,
    ReplayOutputEstimate Estimate)
{
    public ReplayFramePlanSnapshot FramePlan => Entries as ReplayFramePlanSnapshot ??
        throw new InvalidOperationException("The preview was not created from an immutable replay frame plan.");
}

/// <summary>
/// Headless output workspace state. Avalonia and CLI can project controls onto this model without
/// owning a second copy of replay sampling or validation rules.
/// </summary>
public sealed class ReplayOutputWorkspace
{
    public const int MaximumWidth = 7680;
    public const int MaximumHeight = 4320;
    public const int LongExportFrameThreshold = 10_000;
    public static readonly TimeSpan LongExportDurationThreshold = TimeSpan.FromMinutes(2);

    public ReplayOutputWorkspace(ReplayOutputSettings? settings = null)
    {
        Settings = settings ?? ReplayOutputSettings.Default;
    }

    public ReplayOutputSettings Settings { get; private set; }
    public long Revision { get; private set; }

    public bool Update(ReplayOutputSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings == Settings) return false;
        Settings = settings;
        Revision++;
        return true;
    }

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
