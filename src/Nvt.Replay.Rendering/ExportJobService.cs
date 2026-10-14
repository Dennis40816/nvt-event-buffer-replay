using Nvt.Replay.Analysis;

namespace Nvt.Replay.Rendering;

/// <summary>Identifies the lifecycle state of an export job.</summary>
public enum ExportJobStatus
{
    /// <summary>No export job has been started.</summary>
    Idle,
    /// <summary>The export operation is running.</summary>
    Running,
    /// <summary>Cancellation has been requested for the active export job.</summary>
    Cancelling,
    /// <summary>The export operation completed without cancellation or an exception.</summary>
    Succeeded,
    /// <summary>The export job finished after cancellation was requested.</summary>
    Cancelled,
    /// <summary>The export operation ended with an exception.</summary>
    Failed,
}

/// <summary>Captures an export job's status, progress, result and error.</summary>
/// <param name="JobId">The identifier assigned to the export job.</param>
/// <param name="Status">The export job's lifecycle state.</param>
/// <param name="Progress">The latest reported export progress, if available.</param>
/// <param name="Result">The export result when the job succeeds, if available.</param>
/// <param name="Error">The exception captured when the export operation fails, if available.</param>
public sealed record ExportJobSnapshot(
    long JobId,
    ExportJobStatus Status,
    ReplayExportProgress? Progress = null,
    ReplayExportResult? Result = null,
    Exception? Error = null)
{
    /// <summary>Indicates whether this export job is running or cancelling.</summary>
    public bool IsActive => Status is ExportJobStatus.Running or ExportJobStatus.Cancelling;
}

/// <summary>Pairs an export job identifier with a task that yields its final snapshot.</summary>
/// <param name="JobId">The identifier assigned to the export job.</param>
/// <param name="Completion">The task that completes with the export job's final snapshot.</param>
public sealed record ExportJobHandle(long JobId, Task<ExportJobSnapshot> Completion);

/// <summary>
/// Owns one observable export operation. Work is invoked on the thread pool; cancellation and
/// generation checks prevent late progress or a result returned after cancellation from replacing
/// the current state.
/// </summary>
public sealed class ExportJobService
{
    private readonly object gate = new();
    private ActiveJob? active;
    private long nextJobId;
    private ExportJobSnapshot snapshot = new(0, ExportJobStatus.Idle);

    /// <summary>Gets the latest snapshot of the export job's state.</summary>
    public ExportJobSnapshot Snapshot
    {
        get
        {
            lock (gate) return snapshot;
        }
    }

    /// <summary>Indicates whether the current export job is running or cancelling.</summary>
    public bool IsActive => Snapshot.IsActive;

    /// <summary>Starts an export operation on the thread pool and returns its job identifier and completion task.</summary>
    public ExportJobHandle Start(
        Func<CancellationToken, IProgress<ReplayExportProgress>, Task<ReplayExportResult>> operation,
        IProgress<ExportJobSnapshot>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ActiveJob job;
        ExportJobSnapshot started;
        lock (gate)
        {
            if (active is not null)
                throw new InvalidOperationException("An export job is already active.");
            nextJobId = checked(nextJobId + 1);
            job = new ActiveJob(nextJobId, new CancellationTokenSource(), observer);
            active = job;
            started = new ExportJobSnapshot(
                job.JobId,
                ExportJobStatus.Running,
                new ReplayExportProgress(0, 0, "Preparing export"));
            snapshot = started;
        }
        SafeReport(observer, started);
        var completion = Task.Run(() => RunAsync(job, operation, observer));
        return new ExportJobHandle(job.JobId, completion);
    }

    /// <summary>Starts a replay export with the supplied exporter, session, scene factory and options.</summary>
    public ExportJobHandle StartReplayExport(
        ReplayRangeExporter exporter,
        ITouchReplaySession replay,
        Func<int, ReplayScene> sceneFactory,
        ReplayExportOptions options,
        IProgress<ExportJobSnapshot>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        return Start(
            (cancellationToken, progress) => exporter.ExportAsync(
                replay,
                sceneFactory,
                options,
                cancellationToken,
                progress),
            observer);
    }

    /// <summary>Requests cancellation of the active export job and reports whether a new request was made.</summary>
    public bool Cancel()
    {
        ExportJobSnapshot cancelling;
        IProgress<ExportJobSnapshot>? observer;
        ActiveJob job;
        lock (gate)
        {
            if (active is null || snapshot.Status == ExportJobStatus.Cancelling)
                return false;
            snapshot = snapshot with { Status = ExportJobStatus.Cancelling };
            cancelling = snapshot;
            observer = active.Observer;
            job = active;
            job.CancelRequested = true;
        }
        SafeReport(observer, cancelling);
        try
        {
            job.Cancellation.Cancel();
        }
        finally
        {
            var dispose = false;
            lock (gate)
            {
                job.CancelCallFinished = true;
                dispose = job.CompletionFinished;
            }
            if (dispose) job.Cancellation.Dispose();
        }
        return true;
    }

    private async Task<ExportJobSnapshot> RunAsync(
        ActiveJob job,
        Func<CancellationToken, IProgress<ReplayExportProgress>, Task<ReplayExportResult>> operation,
        IProgress<ExportJobSnapshot>? observer)
    {
        var progress = new InlineProgress<ReplayExportProgress>(item => ReportProgress(job.JobId, item, observer));
        ReplayExportResult? result = null;
        Exception? error = null;
        try
        {
            result = await operation(job.Cancellation.Token, progress).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            error = exception;
        }

        var publish = false;
        var dispose = false;
        ExportJobSnapshot completed;
        lock (gate)
        {
            completed = job.CancelRequested || job.Cancellation.IsCancellationRequested
                ? new ExportJobSnapshot(job.JobId, ExportJobStatus.Cancelled, snapshot.Progress)
                : error is not null
                    ? new ExportJobSnapshot(job.JobId, ExportJobStatus.Failed, snapshot.Progress, Error: error)
                    : new ExportJobSnapshot(job.JobId, ExportJobStatus.Succeeded, snapshot.Progress, result);
            if (active?.JobId == job.JobId)
            {
                snapshot = completed;
                active = null;
                publish = true;
            }
            job.CompletionFinished = true;
            dispose = !job.CancelRequested || job.CancelCallFinished;
        }
        if (dispose) job.Cancellation.Dispose();
        if (publish) SafeReport(observer, completed);
        return completed;
    }

    private void ReportProgress(
        long jobId,
        ReplayExportProgress progress,
        IProgress<ExportJobSnapshot>? observer)
    {
        ExportJobSnapshot? update = null;
        lock (gate)
        {
            if (active?.JobId != jobId || !snapshot.IsActive) return;
            snapshot = snapshot with { Progress = progress };
            update = snapshot;
        }
        SafeReport(observer, update);
    }

    private static void SafeReport(IProgress<ExportJobSnapshot>? observer, ExportJobSnapshot? update)
    {
        if (observer is null || update is null) return;
        try
        {
            observer.Report(update);
        }
        catch
        {
            // Progress presentation must never fail or cancel the underlying export.
        }
    }

    private sealed class ActiveJob(
        long jobId,
        CancellationTokenSource cancellation,
        IProgress<ExportJobSnapshot>? observer)
    {
        public long JobId { get; } = jobId;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public IProgress<ExportJobSnapshot>? Observer { get; } = observer;
        public bool CancelRequested { get; set; }
        public bool CancelCallFinished { get; set; }
        public bool CompletionFinished { get; set; }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
