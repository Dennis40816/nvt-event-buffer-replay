using System.Diagnostics;
using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

public sealed record ReplayWorkspaceBuildRequest(
    ITouchReplaySession Replay,
    int ProgressInterval = 1024);

public sealed record ReplayWorkspaceBuildProgress(
    string Phase,
    int CompletedFrames,
    int TotalFrames)
{
    public double Percent => TotalFrames == 0
        ? 100
        : Math.Clamp(CompletedFrames * 100d / TotalFrames, 0, 100);
}

public sealed record ReplayCoordinateExtent(double MaximumX, double MaximumY);

public readonly record struct ReplayFrameState(
    uint ReportedContactMask,
    uint ActiveContactMask,
    bool HasReportedBreak,
    bool AllBreak);

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

    public ITouchReplaySession Replay { get; }

    public ReplayFrameCache Frames { get; }

    public ReplayCoordinateExtent Extent { get; }

    public ReplayAutoPauseIndex AutoPauseIndex { get; }

    public IReadOnlyList<ReplayFrameState> FrameStates { get; }
}

public sealed record ReplayWorkspaceBuildResult(
    ReplayWorkspace Workspace,
    int MaterializedFrames,
    TimeSpan Elapsed);

public sealed class ReplayWorkspaceBuilder
{
    public Task<ReplayWorkspaceBuildResult> BuildAsync(
        ReplayWorkspaceBuildRequest request,
        IProgress<ReplayWorkspaceBuildProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(request, progress, cancellationToken), cancellationToken);

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
