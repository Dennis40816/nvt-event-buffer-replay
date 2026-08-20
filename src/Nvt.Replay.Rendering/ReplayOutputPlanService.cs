using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public delegate ReplayFramePlanSnapshot ReplayOutputPlanBuilder(
    ITouchReplaySession replay,
    ReplayOutputSettings settings,
    CancellationToken cancellationToken,
    IProgress<ReplayExportProgress>? progress);

/// <summary>
/// Builds and caches one immutable output sampling plan without blocking the caller. Requests with
/// the same sampling identity share the same task; a changed identity cancels superseded planning.
/// </summary>
public sealed class ReplayOutputPlanService : IDisposable
{
    private readonly object gate = new();
    private readonly ReplayOutputPlanBuilder builder;
    private ActivePlan? active;
    private long nextGeneration;
    private int buildCount;
    private bool disposed;

    public ReplayOutputPlanService(ReplayOutputPlanBuilder? builder = null) =>
        this.builder = builder ?? BuildDefault;

    public int BuildCount => Volatile.Read(ref buildCount);

    public long ActiveGeneration
    {
        get
        {
            lock (gate) return active?.Generation ?? 0;
        }
    }

    public Task<ReplayOutputPreviewPlan> GetPreviewAsync(
        ITouchReplaySession replay,
        string replayIdentity,
        ReplayOutputSettings settings,
        CancellationToken cancellationToken = default,
        IProgress<ReplayExportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(replay);
        if (string.IsNullOrWhiteSpace(replayIdentity))
            throw new ArgumentException("Replay identity is required.", nameof(replayIdentity));
        var workspace = new ReplayOutputWorkspace(settings);
        var validation = workspace.Validate(replay);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join(' ', validation.Errors), nameof(settings));

        var identity = new ReplayOutputPlanIdentity(
            replayIdentity,
            replay.Count,
            replay.FrameInterval,
            settings);
        var samplingIdentity = identity.SamplingIdentity;
        ActivePlan request;
        CancellationTokenSource? superseded = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (active is { } current &&
                ReferenceEquals(current.Replay, replay) &&
                current.Identity == samplingIdentity)
            {
                request = current;
            }
            else
            {
                superseded = active?.Cancellation;
                var generation = checked(++nextGeneration);
                var planningCancellation = new CancellationTokenSource();
                var planningTask = Task.Run(
                    () => builder(replay, settings, planningCancellation.Token, progress),
                    CancellationToken.None);
                request = new ActivePlan(
                    generation,
                    replay,
                    samplingIdentity,
                    planningCancellation,
                    planningTask);
                active = request;
                Interlocked.Increment(ref buildCount);
            }
        }
        CancelAndDisposeWhenComplete(superseded, requestToIgnore: request);
        return AwaitPreviewAsync(request, replayIdentity, settings, cancellationToken);
    }

    public void Invalidate()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            cancellation = active?.Cancellation;
            active = null;
        }
        CancelAndDisposeWhenComplete(cancellation);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancellation = active?.Cancellation;
            active = null;
        }
        CancelAndDisposeWhenComplete(cancellation);
    }

    private async Task<ReplayOutputPreviewPlan> AwaitPreviewAsync(
        ActivePlan request,
        string replayIdentity,
        ReplayOutputSettings settings,
        CancellationToken cancellationToken)
    {
        var plan = await request.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (gate)
        {
            if (active?.Generation != request.Generation)
                throw new OperationCanceledException("Output planning was superseded by newer sampling settings.");
        }
        return new ReplayOutputWorkspace(settings).CreateVideoPreview(request.Replay, replayIdentity, plan);
    }

    private static ReplayFramePlanSnapshot BuildDefault(
        ITouchReplaySession replay,
        ReplayOutputSettings settings,
        CancellationToken cancellationToken,
        IProgress<ReplayExportProgress>? progress) =>
        ReplayFramePlan.BuildSnapshot(
            replay,
            new ReplayOutputWorkspace(settings).CreateExportOptions("preview.mp4"),
            cancellationToken,
            progress);

    private void CancelAndDisposeWhenComplete(
        CancellationTokenSource? cancellation,
        ActivePlan? requestToIgnore = null)
    {
        if (cancellation is null || ReferenceEquals(cancellation, requestToIgnore?.Cancellation)) return;
        try
        {
            cancellation.Cancel();
        }
        finally
        {
            ActivePlan? owner;
            lock (gate) owner = active is { Cancellation: var current } && ReferenceEquals(current, cancellation)
                ? active
                : null;
            if (owner is null)
            {
                // CancellationTokenSource may still be observed by a superseded builder. Dispose
                // only after that builder has left its task.
                cancellation.Dispose();
            }
        }
    }

    private sealed record ActivePlan(
        long Generation,
        ITouchReplaySession Replay,
        ReplayOutputSamplingIdentity Identity,
        CancellationTokenSource Cancellation,
        Task<ReplayFramePlanSnapshot> Task);
}
