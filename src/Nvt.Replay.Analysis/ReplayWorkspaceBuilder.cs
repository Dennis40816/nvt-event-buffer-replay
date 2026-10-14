using System.Diagnostics;
using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

/// <summary>Specifies the replay session and progress reporting interval for building a workspace.</summary>
/// <param name="Replay">The replay session whose snapshots are materialized.</param>
/// <param name="ProgressInterval">The number of frames between progress reports while materializing snapshots.</param>
public sealed record ReplayWorkspaceBuildRequest(
    ITouchReplaySession Replay,
    int ProgressInterval = 1024);

/// <summary>Describes the current build phase and the number of replay frames materialized.</summary>
/// <param name="Phase">The description of the current workspace build phase.</param>
/// <param name="CompletedFrames">The number of replay frames materialized so far.</param>
/// <param name="TotalFrames">The total frame count declared by the replay session.</param>
public sealed record ReplayWorkspaceBuildProgress(
    string Phase,
    int CompletedFrames,
    int TotalFrames)
{
    /// <summary>Gets the materialized frame percentage clamped to 0 to 100, or 100 when the total frame count is zero.</summary>
    public double Percent => TotalFrames == 0
        ? 100
        : Math.Clamp(CompletedFrames * 100d / TotalFrames, 0, 100);
}

/// <summary>Stores the maximum X and Y coordinate bounds for a replay workspace.</summary>
/// <param name="MaximumX">The upper X coordinate bound.</param>
/// <param name="MaximumY">The upper Y coordinate bound.</param>
public sealed record ReplayCoordinateExtent(double MaximumX, double MaximumY);

/// <summary>Stores contact ID masks and break flags derived from a replay snapshot.</summary>
/// <param name="ReportedContactMask">The bit mask of reported contact IDs below 32.</param>
/// <param name="ActiveContactMask">The bit mask of active host contact IDs below 32.</param>
/// <param name="HasReportedBreak">Whether any reported contact that is not invalid has Break status.</param>
/// <param name="AllBreak">Whether the replay snapshot reports an all-break state.</param>
public readonly record struct ReplayFrameState(
    uint ReportedContactMask,
    uint ActiveContactMask,
    bool HasReportedBreak,
    bool AllBreak);

/// <summary>Holds a replay session, cached snapshots, coordinate bounds, auto-pause indices, and frame states.</summary>
public sealed class ReplayWorkspace
{
    internal ReplayWorkspace(
        ITouchReplaySession replay,
        ReplayFrameCache frames,
        ReplayCoordinateExtent extent,
        ReplayAutoPauseIndex autoPauseIndex,
        ReplayFrameState[] frameStates)
    {
        Replay = replay;
        Frames = frames;
        Extent = extent;
        AutoPauseIndex = autoPauseIndex;
        FrameStates = frameStates;
    }

    /// <summary>Gets the replay session used to build this workspace.</summary>
    public ITouchReplaySession Replay { get; }

    /// <summary>Gets the cache of materialized replay snapshots.</summary>
    public ReplayFrameCache Frames { get; }

    /// <summary>Gets the padded and rounded coordinate bounds derived from reported contacts.</summary>
    public ReplayCoordinateExtent Extent { get; }

    /// <summary>Gets the indices of frames with reported breaks or an all-break state.</summary>
    public ReplayAutoPauseIndex AutoPauseIndex { get; }

    /// <summary>Gets the contact masks and break flags for each replay frame in logical order.</summary>
    public IReadOnlyList<ReplayFrameState> FrameStates { get; }
}

/// <summary>Contains the built workspace, materialized frame count, and elapsed build time.</summary>
/// <param name="Workspace">The workspace created from the replay session.</param>
/// <param name="MaterializedFrames">The number of snapshots materialized while building the workspace.</param>
/// <param name="Elapsed">The time elapsed while building the workspace.</param>
public sealed record ReplayWorkspaceBuildResult(
    ReplayWorkspace Workspace,
    int MaterializedFrames,
    TimeSpan Elapsed);

/// <summary>Materializes replay snapshots and builds frame states, coordinate bounds, and auto-pause indices.</summary>
public sealed class ReplayWorkspaceBuilder
{
    /// <summary>Builds a replay workspace on a thread pool task with progress reporting and cancellation.</summary>
    public Task<ReplayWorkspaceBuildResult> BuildAsync(
        ReplayWorkspaceBuildRequest request,
        IProgress<ReplayWorkspaceBuildProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(request, progress, cancellationToken), cancellationToken);

    /// <summary>Materializes and validates replay frames, then creates a workspace with bounds and auto-pause indices.</summary>
    public ReplayWorkspaceBuildResult Build(
        ReplayWorkspaceBuildRequest request,
        IProgress<ReplayWorkspaceBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Replay);
        if (request.ProgressInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Progress interval must be positive.");

        var started = Stopwatch.GetTimestamp();
        var replay = request.Replay;
        var snapshots = new ITouchReplaySnapshot[replay.Count];
        var frameStates = new ReplayFrameState[replay.Count];
        var breakIndices = new List<int>();
        var allBreakIndices = new List<int>();
        double maximumX = 1920;
        double maximumY = 1080;
        var completed = 0;

        progress?.Report(new ReplayWorkspaceBuildProgress("Materializing replay", 0, replay.Count));
        foreach (var snapshot in replay.EnumerateSnapshots(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed >= snapshots.Length)
                throw new InvalidDataException("Replay produced more snapshots than its declared frame count.");
            if (snapshot.LogicalIndex != completed)
                throw new InvalidDataException(
                    $"Replay snapshot order is not linear: expected logical index {completed}, received {snapshot.LogicalIndex}.");

            snapshots[completed] = snapshot;
            var reportedMask = ContactMask(snapshot.ReportedContacts);
            var activeMask = ContactMask(snapshot.HostContacts.Where(contact => contact.IsActive));
            var hasBreak = snapshot.ReportedContacts.Any(contact =>
                !contact.Invalid && contact.Status == TouchStatus.Break);
            frameStates[completed] = new ReplayFrameState(reportedMask, activeMask, hasBreak, snapshot.AllBreak);
            if (hasBreak) breakIndices.Add(completed);
            if (snapshot.AllBreak) allBreakIndices.Add(completed);

            foreach (var contact in snapshot.ReportedContacts)
            {
                maximumX = Math.Max(maximumX, contact.X);
                maximumY = Math.Max(maximumY, contact.Y);
            }

            completed++;
            if (completed == replay.Count || completed % request.ProgressInterval == 0)
                progress?.Report(new ReplayWorkspaceBuildProgress("Materializing replay", completed, replay.Count));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (completed != replay.Count)
            throw new InvalidDataException(
                $"Replay produced {completed} snapshots but declared {replay.Count} frames.");

        var workspace = new ReplayWorkspace(
            replay,
            ReplayFrameCache.FromOwnedSnapshots(snapshots),
            new ReplayCoordinateExtent(NiceExtent(maximumX, 1920), NiceExtent(maximumY, 1080)),
            ReplayAutoPauseIndex.FromOwnedSortedIndices(breakIndices.ToArray(), allBreakIndices.ToArray()),
            frameStates);
        progress?.Report(new ReplayWorkspaceBuildProgress("Ready", completed, replay.Count));
        return new ReplayWorkspaceBuildResult(workspace, completed, Stopwatch.GetElapsedTime(started));
    }

    private static uint ContactMask(IEnumerable<ReplayContact> contacts)
    {
        uint mask = 0;
        foreach (var contact in contacts)
        {
            if (contact.Id < 32) mask |= 1u << contact.Id;
        }
        return mask;
    }

    private static double NiceExtent(double maximum, double minimum)
    {
        var required = Math.Max(minimum, maximum * 1.08);
        return Math.Ceiling(required / 256) * 256;
    }
}
