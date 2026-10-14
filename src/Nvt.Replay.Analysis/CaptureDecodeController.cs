namespace Nvt.Replay.Analysis;

/// <summary>Supplies a source file and format selection for a capture-to-workspace decode operation.</summary>
/// <param name="SourcePath">Path of the source file to load.</param>
/// <param name="Format">Format selection supplied to executable format resolution.</param>
/// <param name="SourceAdapterId">Optional source adapter identifier passed to capture loading.</param>
/// <param name="WorkspaceProgressInterval">Progress interval supplied to the workspace builder.</param>
public sealed record CaptureDecodeRequest(
    string SourcePath,
    FormatDecodeRequest Format,
    string? SourceAdapterId = null,
    int WorkspaceProgressInterval = 1024);

/// <summary>Supplies an existing capture and format selection for a capture-to-workspace decode operation.</summary>
/// <param name="Capture">Loaded capture supplied directly to the decode operation.</param>
/// <param name="Format">Format selection supplied to executable format resolution.</param>
/// <param name="WorkspaceProgressInterval">Progress interval supplied to the workspace builder.</param>
public sealed record PreparedCaptureDecodeRequest(
    CaptureSession Capture,
    FormatDecodeRequest Format,
    int WorkspaceProgressInterval = 1024);

/// <summary>Identifies a stage of loading, decoding, and building a replay workspace from a capture.</summary>
public enum CaptureDecodePhase
{
    /// <summary>Indicates that source probing is in progress.</summary>
    ProbingSource,
    /// <summary>Indicates that source hashing is in progress.</summary>
    HashingSource,
    /// <summary>Indicates that source records are being indexed.</summary>
    IndexingRecords,
    /// <summary>Indicates that source loading has reached the ready-for-configuration stage.</summary>
    SourceReady,
    /// <summary>Indicates that the executable format is being resolved for the capture.</summary>
    SelectingFormat,
    /// <summary>Indicates that the selected register profile is being applied to the capture.</summary>
    ProjectingRegisters,
    /// <summary>Indicates that the capture is being decoded with the selected format.</summary>
    DecodingFrames,
    /// <summary>Indicates that decoded replay frames are being materialized into a workspace.</summary>
    BuildingWorkspace,
    /// <summary>Indicates that workspace construction is complete.</summary>
    Ready,
}

/// <summary>Reports the generation, phase, and item counts of a capture decode operation.</summary>
/// <param name="Generation">Generation of the capture decode operation reporting progress.</param>
/// <param name="Phase">Current stage of the capture decode operation.</param>
/// <param name="CompletedItems">Number of items reported as completed in the current phase.</param>
/// <param name="TotalItems">Optional total item count used to calculate phase progress.</param>
public sealed record CaptureDecodeProgress(
    long Generation,
    CaptureDecodePhase Phase,
    int CompletedItems = 0,
    int? TotalItems = null)
{
    /// <summary>Gets the clamped phase percentage, or 100 when ready without a positive total, otherwise null.</summary>
    public double? Percent => TotalItems is > 0
        ? Math.Clamp(CompletedItems * 100d / TotalItems.Value, 0, 100)
        : Phase == CaptureDecodePhase.Ready
            ? 100
            : null;
}

/// <summary>Holds the loaded capture, decode output, and replay workspace build result for an operation.</summary>
/// <param name="Generation">Generation of the operation that produced this result.</param>
/// <param name="LoadedCapture">Capture supplied by source loading or a prepared request before register projection.</param>
/// <param name="Decode">Result of decoding the capture with the selected format.</param>
/// <param name="WorkspaceBuild">Result of building a replay workspace from the decoded replay.</param>
public sealed record CaptureDecodeResult(
    long Generation,
    CaptureSession LoadedCapture,
    ExecutableFormatDecodeResult Decode,
    ReplayWorkspaceBuildResult WorkspaceBuild)
{
    /// <summary>Gets the capture returned by the selected format decoder.</summary>
    public CaptureSession Capture => Decode.Capture;

    /// <summary>Gets the replay workspace produced from the decoded replay.</summary>
    public ReplayWorkspace Workspace => WorkspaceBuild.Workspace;
}

/// <summary>Exposes the generation and completion task of a started capture decode operation.</summary>
public sealed class CaptureDecodeOperation
{
    internal CaptureDecodeOperation(long generation, Task<CaptureDecodeResult> completion)
    {
        Generation = generation;
        Completion = completion;
    }

    /// <summary>Gets the generation assigned to this capture decode operation.</summary>
    public long Generation { get; }

    /// <summary>Gets the task that completes with the decoded capture and replay workspace.</summary>
    public Task<CaptureDecodeResult> Completion { get; }
}

/// <summary>Reports a failure to resolve an executable format for a capture.</summary>
public sealed class CaptureDecodeConfigurationException(string message) : ArgumentException(message);

/// <summary>Signals that an operation was cancelled or superseded before its result could be published.</summary>
public sealed class CaptureDecodeSupersededException(long generation)
    : OperationCanceledException($"Capture decode operation {generation} was superseded or cancelled before publication.")
{
    /// <summary>Gets the generation of the operation whose result was rejected.</summary>
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

    /// <summary>Creates a capture decode controller using the supplied workspace builder or a default builder.</summary>
    public CaptureDecodeController(ReplayWorkspaceBuilder? workspaceBuilder = null) =>
        this.workspaceBuilder = workspaceBuilder ?? new ReplayWorkspaceBuilder();

    /// <summary>Gets the last result accepted for publication, or null before the first successful publication.</summary>
    public CaptureDecodeResult? LastSuccessfulResult
    {
        get
        {
            lock (gate) return lastSuccessfulResult;
        }
    }

    /// <summary>Gets the active capture decode operation's generation, or null when no operation is active.</summary>
    public long? ActiveGeneration
    {
        get
        {
            lock (gate) return activeOperation?.Generation;
        }
    }

    /// <summary>Loads and decodes a source file, builds its replay workspace, and supersedes any active operation.</summary>
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

    /// <summary>Decodes an existing capture, builds its replay workspace, and supersedes any active operation.</summary>
    public Task<CaptureDecodeResult> RunAsync(
        PreparedCaptureDecodeRequest request,
        IProgress<CaptureDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        StartPrepared(request, progress, cancellationToken).Completion;

    /// <summary>Starts decoding an existing capture and building its workspace, exposing its generation and completion task.</summary>
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

    /// <summary>Cancels and clears the active decode operation, returning whether an operation was active.</summary>
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

    /// <inheritdoc />
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
            if (!ExecutableFormatRegistry.TryResolve(
                    loadedCapture,
                    format,
                    out var selection,
                    out _,
                    out var error))
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
