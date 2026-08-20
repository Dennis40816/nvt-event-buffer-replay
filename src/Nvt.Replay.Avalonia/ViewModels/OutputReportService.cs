using Nvt.Replay.Analysis;

namespace Nvt.Replay.Avalonia.ViewModels;

internal sealed record OutputReportIdentity(
    CaptureSession Capture,
    ITouchReplaySession Replay,
    ReplayDecodeConfiguration DecodeConfiguration,
    string ReplayIdentity,
    AnalysisRange Range,
    long ReportRevision)
{
    public bool Matches(OutputReportIdentity other) =>
        ReferenceEquals(Capture, other.Capture) &&
        ReferenceEquals(Replay, other.Replay) &&
        DecodeConfiguration == other.DecodeConfiguration &&
        ReplayIdentity == other.ReplayIdentity &&
        Range == other.Range &&
        ReportRevision == other.ReportRevision;
}

internal sealed record OutputReportSnapshot(
    OutputReportIdentity Identity,
    OutputReportInputs Inputs,
    CaptureAnalysisReport Report);

internal delegate CaptureAnalysisReport OutputReportBuilder(
    OutputReportInputs inputs,
    CancellationToken cancellationToken);

/// <summary>
/// Owns one frozen, single-flight analysis operation and one last successful result. Individual
/// callers may stop waiting without cancelling peers; workspace generation changes explicitly
/// cancel the shared operation. Failed and cancelled operations never replace the reusable cache.
/// </summary>
internal sealed class OutputReportService : IDisposable
{
    private readonly object gate = new();
    private readonly OutputReportBuilder builder;
    private ActiveRequest? active;
    private OutputReportSnapshot? completed;
    private long nextGeneration;
    private int buildCount;
    private bool disposed;

    public OutputReportService(OutputReportBuilder? builder = null) =>
        this.builder = builder ?? BuildDefault;

    public int BuildCount => Volatile.Read(ref buildCount);

    public long ActiveGeneration
    {
        get
        {
            lock (gate) return active?.Generation ?? 0;
        }
    }

    public Task<OutputReportSnapshot> GetAsync(
        OutputReportIdentity identity,
        Func<OutputReportInputs> inputsFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(inputsFactory);
        cancellationToken.ThrowIfCancellationRequested();
        ActiveRequest request;
        ActiveRequest? superseded = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed is { } cached && cached.Identity.Matches(identity))
                return Task.FromResult(cached);
            if (active is { } current && current.Identity.Matches(identity))
            {
                request = current;
            }
            else
            {
                superseded = active;
                var inputs = inputsFactory();
                ValidateInputs(identity, inputs);
                var generation = checked(++nextGeneration);
                var operationCancellation = new CancellationTokenSource();
                var completion = new TaskCompletionSource<OutputReportSnapshot>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                request = new ActiveRequest(
                    generation,
                    identity,
                    inputs,
                    operationCancellation,
                    completion);
                active = request;
                Interlocked.Increment(ref buildCount);
                _ = Task.Run(() => Run(request), CancellationToken.None);
            }
        }
        Cancel(superseded);
        return request.Completion.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Cancels hidden work while preserving the last completed report for later reuse.</summary>
    public void CancelActive()
    {
        ActiveRequest? request;
        lock (gate)
        {
            request = active;
            active = null;
        }
        Cancel(request);
    }

    /// <summary>Cancels work and drops results tied to a capture/review generation.</summary>
    public void Reset()
    {
        ActiveRequest? request;
        lock (gate)
        {
            request = active;
            active = null;
            completed = null;
        }
        Cancel(request);
    }

    public void Dispose()
    {
        ActiveRequest? request;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            request = active;
            active = null;
            completed = null;
        }
        Cancel(request);
    }

    private void Run(ActiveRequest request)
    {
        try
        {
            request.Cancellation.Token.ThrowIfCancellationRequested();
            var report = builder(request.Inputs, request.Cancellation.Token);
            request.Cancellation.Token.ThrowIfCancellationRequested();
            var result = new OutputReportSnapshot(request.Identity, request.Inputs, report);
            var publish = false;
            lock (gate)
            {
                if (active?.Generation == request.Generation)
                {
                    completed = result;
                    active = null;
                    publish = true;
                }
            }
            if (publish) request.Completion.TrySetResult(result);
            else request.Completion.TrySetCanceled(new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            ClearIfActive(request);
            request.Completion.TrySetCanceled(request.Cancellation.Token);
        }
        catch (Exception exception)
        {
            ClearIfActive(request);
            request.Completion.TrySetException(exception);
        }
        finally
        {
            request.Cancellation.Dispose();
        }
    }

    private static CaptureAnalysisReport BuildDefault(
        OutputReportInputs inputs,
        CancellationToken cancellationToken) => inputs.Build(cancellationToken);

    private void ClearIfActive(ActiveRequest request)
    {
        lock (gate)
        {
            if (active?.Generation == request.Generation) active = null;
        }
    }

    private static void Cancel(ActiveRequest? request)
    {
        if (request is null) return;
        CancellationToken token;
        try
        {
            token = request.Cancellation.Token;
            request.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker completed between detaching the request and cancellation.
            token = new CancellationToken(canceled: true);
        }
        request.Completion.TrySetCanceled(token);
    }

    private static void ValidateInputs(OutputReportIdentity identity, OutputReportInputs inputs)
    {
        if (!ReferenceEquals(identity.Capture, inputs.Capture) ||
            !ReferenceEquals(identity.Replay, inputs.Replay) ||
            identity.DecodeConfiguration != inputs.DecodeConfiguration ||
            identity.Range != inputs.Range ||
            identity.ReportRevision != inputs.ReportRevision)
            throw new InvalidOperationException("Frozen output report inputs do not match the requested report identity.");
    }

    private sealed record ActiveRequest(
        long Generation,
        OutputReportIdentity Identity,
        OutputReportInputs Inputs,
        CancellationTokenSource Cancellation,
        TaskCompletionSource<OutputReportSnapshot> Completion);
}
