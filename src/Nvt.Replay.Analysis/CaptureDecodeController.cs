namespace Nvt.Replay.Analysis;

public sealed record CaptureDecodeRequest(
    string SourcePath,
    FormatDecodeRequest Format,
    string? SourceAdapterId = null,
    int WorkspaceProgressInterval = 1024);

public sealed record PreparedCaptureDecodeRequest(
    CaptureSession Capture,
    FormatDecodeRequest Format,
    int WorkspaceProgressInterval = 1024);

public enum CaptureDecodePhase
{
    ProbingSource,
    HashingSource,
    IndexingRecords,
    SourceReady,
    SelectingFormat,
    ProjectingRegisters,
    DecodingFrames,
    BuildingWorkspace,
    Ready,
}

public sealed record CaptureDecodeProgress(
    long Generation,
    CaptureDecodePhase Phase,
    int CompletedItems = 0,
    int? TotalItems = null)
{
    public double? Percent => TotalItems is > 0
        ? Math.Clamp(CompletedItems * 100d / TotalItems.Value, 0, 100)
        : Phase == CaptureDecodePhase.Ready
            ? 100
            : null;
}

public sealed record CaptureDecodeResult(
    long Generation,
    CaptureSession LoadedCapture,
    ExecutableFormatDecodeResult Decode,
    ReplayWorkspaceBuildResult WorkspaceBuild)
{
    public CaptureSession Capture => Decode.Capture;

    public ReplayWorkspace Workspace => WorkspaceBuild.Workspace;
}

public sealed class CaptureDecodeOperation
{
    internal CaptureDecodeOperation(long generation, Task<CaptureDecodeResult> completion)
    {
        Generation = generation;
        Completion = completion;
    }

    public long Generation { get; }

    public Task<CaptureDecodeResult> Completion { get; }
}

public sealed class CaptureDecodeConfigurationException(string message) : ArgumentException(message);

public sealed class CaptureDecodeSupersededException(long generation)
    : OperationCanceledException($"Capture decode operation {generation} was superseded or cancelled before publication.")
{
    public long Generation { get; } = generation;
}

/// <summary>
/// Owns the complete headless capture-to-workspace operation. Results are published atomically:
/// cancellation, failure, or a stale completion never replaces the last successful result.
/// </summary>
public sealed class CaptureDecodeController : IDisposable
{
    private readonly object gate = new();
    private readonly ReplayWorkspaceBuilder workspaceBuilder;
    private ActiveOperation? activeOperation;
    private CaptureDecodeResult? lastSuccessfulResult;
    private long nextGeneration;
    private bool disposed;

    public CaptureDecodeController(ReplayWorkspaceBuilder? workspaceBuilder = null) =>
        this.workspaceBuilder = workspaceBuilder ?? new ReplayWorkspaceBuilder();

    public CaptureDecodeResult? LastSuccessfulResult
    {
        get
        {
            lock (gate) return lastSuccessfulResult;
        }
    }

    public long? ActiveGeneration
    {
        get
        {
            lock (gate) return activeOperation?.Generation;
        }
    }

    public Task<CaptureDecodeResult> RunAsync(
        CaptureDecodeRequest request,
        IProgress<CaptureDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        return StartOperation(
            cancellationToken,
            operation => ExecuteAsync(
                operation,
                request.Format,
                request.WorkspaceProgressInterval,
                progress,
                token => LoadCaptureAsync(operation, request, progress, token))).Completion;
    }

    public Task<CaptureDecodeResult> RunAsync(
        PreparedCaptureDecodeRequest request,
        IProgress<CaptureDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        StartPrepared(request, progress, cancellationToken).Completion;

    public CaptureDecodeOperation StartPrepared(
        PreparedCaptureDecodeRequest request,
        IProgress<CaptureDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);

        return StartOperation(
            cancellationToken,
            operation => ExecuteAsync(
                operation,
                request.Format,
                request.WorkspaceProgressInterval,
                progress,
                _ => Task.FromResult(request.Capture)));
    }

    private CaptureDecodeOperation StartOperation(
        CancellationToken cancellationToken,
        Func<ActiveOperation, Task<CaptureDecodeResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        ActiveOperation operation;
        ActiveOperation? superseded;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            superseded = activeOperation;
            operation = new ActiveOperation(
                checked(++nextGeneration),
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            activeOperation = operation;
        }

        TryCancel(superseded);
        var completion = Task.Run(
            () => execute(operation),
            CancellationToken.None);
        return new CaptureDecodeOperation(operation.Generation, completion);
    }

    public bool CancelCurrent()
    {
        ActiveOperation? operation;
        lock (gate)
        {
            operation = activeOperation;
            activeOperation = null;
        }

        TryCancel(operation);
        return operation is not null;
    }

    public void Dispose()
    {
        ActiveOperation? operation;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            operation = activeOperation;
            activeOperation = null;
        }

        TryCancel(operation);
    }

    private async Task<CaptureDecodeResult> ExecuteAsync(
        ActiveOperation operation,
        FormatDecodeRequest format,
        int workspaceProgressInterval,
        IProgress<CaptureDecodeProgress>? progress,
        Func<CancellationToken, Task<CaptureSession>> captureProvider)
    {
        var cancellationToken = operation.Cancellation.Token;
        try
        {
            var loadedCapture = await captureProvider(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, operation.Generation, CaptureDecodePhase.SelectingFormat);
            if (!ExecutableFormatRegistry.TryResolve(format, out var selection, out var error))
                throw new CaptureDecodeConfigurationException(error);

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, operation.Generation, CaptureDecodePhase.ProjectingRegisters);
            var configuredCapture = loadedCapture.WithRegisterProfile(selection!.RegisterProfile, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            Report(progress, operation.Generation, CaptureDecodePhase.DecodingFrames);
            var decoded = selection.Decode(configuredCapture, cancellationToken);
            if (!ReferenceEquals(loadedCapture.Records, decoded.Capture.Records))
                throw new InvalidDataException("Register projection replaced source records; raw provenance must remain immutable.");

            var workspaceProgress = progress is null
                ? null
                : new ForwardingProgress<ReplayWorkspaceBuildProgress>(item => progress.Report(
                    new CaptureDecodeProgress(
                        operation.Generation,
                        CaptureDecodePhase.BuildingWorkspace,
                        item.CompletedFrames,
                        item.TotalFrames)));
            var workspaceBuild = await workspaceBuilder.BuildAsync(
                new ReplayWorkspaceBuildRequest(decoded.Replay, workspaceProgressInterval),
                workspaceProgress,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var result = new CaptureDecodeResult(
                operation.Generation,
                loadedCapture,
                decoded,
                workspaceBuild);
            Report(
                progress,
                operation.Generation,
                CaptureDecodePhase.Ready,
                workspaceBuild.MaterializedFrames,
                workspaceBuild.MaterializedFrames);
            Publish(operation, result);
            return result;
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(activeOperation, operation)) activeOperation = null;
            }
            operation.Cancellation.Dispose();
        }
    }

    private void Publish(ActiveOperation operation, CaptureDecodeResult result)
    {
        lock (gate)
        {
            if (disposed ||
                !ReferenceEquals(activeOperation, operation) ||
                operation.Cancellation.IsCancellationRequested)
            {
                throw new CaptureDecodeSupersededException(operation.Generation);
            }
            lastSuccessfulResult = result;
            activeOperation = null;
        }
    }

    private static void Validate(CaptureDecodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentNullException.ThrowIfNull(request.Format);
        if (request.WorkspaceProgressInterval <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Workspace progress interval must be positive.");
    }

    private static void Validate(PreparedCaptureDecodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Capture);
        ArgumentNullException.ThrowIfNull(request.Format);
        if (request.WorkspaceProgressInterval <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Workspace progress interval must be positive.");
    }

    private static Task<CaptureSession> LoadCaptureAsync(
        ActiveOperation operation,
        CaptureDecodeRequest request,
        IProgress<CaptureDecodeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var loadProgress = progress is null
            ? null
            : new ForwardingProgress<CaptureLoadProgress>(item => progress.Report(new CaptureDecodeProgress(
                operation.Generation,
                LoadPhase(item.Phase),
                item.RecordsRead)));
        return CaptureSession.LoadAsync(
            request.SourcePath,
            loadProgress,
            cancellationToken,
            request.SourceAdapterId);
    }

    private static CaptureDecodePhase LoadPhase(string phase) => phase switch
    {
        "Probing source" => CaptureDecodePhase.ProbingSource,
        "Hashing source" => CaptureDecodePhase.HashingSource,
        "Indexing records" => CaptureDecodePhase.IndexingRecords,
        "Ready for configuration" => CaptureDecodePhase.SourceReady,
        _ => throw new InvalidDataException($"Unknown capture-load phase '{phase}'."),
    };

    private static void Report(
        IProgress<CaptureDecodeProgress>? progress,
        long generation,
        CaptureDecodePhase phase,
        int completedItems = 0,
        int? totalItems = null) =>
        progress?.Report(new CaptureDecodeProgress(generation, phase, completedItems, totalItems));

    private static void TryCancel(ActiveOperation? operation)
    {
        if (operation is null) return;
        try
        {
            operation.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation won its completion race and disposed its linked source.
        }
    }

    private sealed record ActiveOperation(long Generation, CancellationTokenSource Cancellation);

    private sealed class ForwardingProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
