using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

/// <summary>Builds an output frame sampling snapshot for a replay using the supplied settings.</summary>
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

    /// <summary>Creates a planning service using the supplied builder or the default frame plan builder.</summary>
    public ReplayOutputPlanService(ReplayOutputPlanBuilder? builder = null) =>
        this.builder = builder ?? BuildDefault;

    /// <summary>Gets the number of output plan builds started by this service.</summary>
    public int BuildCount => Volatile.Read(ref buildCount);

    /// <summary>Gets the generation of the active output plan, or zero when no plan is active.</summary>
    public long ActiveGeneration
    {
        get
        {
            lock (gate) return active?.Generation ?? 0;
        }
    }

    /// <summary>Gets a video preview asynchronously, sharing a sampling plan when the replay and sampling identity match.</summary>
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
        ActivePlan? superseded = null;
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
                superseded = active;
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
        CancelAndDisposeWhenComplete(superseded);
        return AwaitPreviewAsync(request, replayIdentity, settings, cancellationToken);
    }

    /// <summary>Clears the active output plan and requests cancellation of its build.</summary>
    public void Invalidate()
    {
        ActivePlan? request;
        lock (gate)
        {
            request = active;
            active = null;
        }
        CancelAndDisposeWhenComplete(request);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ActivePlan? request;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            request = active;
            active = null;
        }
        CancelAndDisposeWhenComplete(request);
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

    private static void CancelAndDisposeWhenComplete(ActivePlan? request)
    {
        if (request is null) return;
        request.Cancellation.Cancel();
        _ = request.Task.ContinueWith(
            (_, state) => ((CancellationTokenSource)state!).Dispose(),
            request.Cancellation,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed record ActivePlan(
        long Generation,
        ITouchReplaySession Replay,
        ReplayOutputSamplingIdentity Identity,
        CancellationTokenSource Cancellation,
        Task<ReplayFramePlanSnapshot> Task);
}
