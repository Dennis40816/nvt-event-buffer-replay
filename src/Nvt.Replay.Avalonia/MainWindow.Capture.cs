using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Avalonia.Controls;
using Nvt.Replay.Avalonia.Views;

namespace Nvt.Replay.Avalonia;

public partial class MainWindow : Window
{
    private const string ProductWindowTitle = "NVT Event Buffer Replay";

    private CancellationTokenSource? operationCancellation;

    private readonly CaptureWorkspaceViewModel _captureWorkspace = new();
    private readonly ReplayShellViewModel _replayShell;

    private CaptureSession? session => _captureWorkspace.Capture?.Session;
    private ITouchReplaySession? replaySession => _captureWorkspace.Replay?.Session;
    private ReplayFrameCache? replayFrames => _captureWorkspace.Replay?.Frames;
    private IReadOnlyList<RawRecordRow> allRawRows => _captureWorkspace.Capture?.Raw.Rows ?? [];
    private IReadOnlyList<RegisterActivityEntry> registerActivities => _captureWorkspace.Capture?.Raw.Activities ?? [];
    private IReadOnlyDictionary<string, RawRecordRow> rawRowsById => _captureWorkspace.Capture?.Raw.RowsById ?? System.Collections.Frozen.FrozenDictionary<string, RawRecordRow>.Empty;
    private IReadOnlyDictionary<string, DecodedFrameRow> decodedRowsBySourceId => _captureWorkspace.Replay?.RowsBySourceId ?? System.Collections.Frozen.FrozenDictionary<string, DecodedFrameRow>.Empty;
    private IReadOnlyList<DecodedFrameRow> decodedRows => _captureWorkspace.Replay?.Rows ?? [];
    private IReadOnlyList<DiagnosticRow> diagnosticRows => _captureWorkspace.Capture?.Diagnostics.Rows ?? [];
    private IReadOnlyList<int> diagnosticLineNumbers => _captureWorkspace.Capture?.Diagnostics.LineNumbers ?? [];
    private ReplayDecodeConfiguration? decodeConfiguration => _captureWorkspace.Replay?.Configuration;
    private ReviewInspectorWorkspace? reviewWorkspace => _captureWorkspace.Capture?.Review;

    private readonly CaptureDecodeController captureDecodeController = new();

    private readonly EventSuppressionScope configuringEventVersion = new();

    private readonly EventSuppressionScope configuringSourceChoice = new();

    private readonly EventSuppressionScope configuringRegisterProfile = new();

    private NvtRegisterProfileInferenceResult? pendingRegisterProfileInference => _captureWorkspace.PendingConfiguration;

    private int targetI2cAddress = 0x01;

    private bool operationInProgress;

    internal Action<CaptureLoadProgress>? CaptureLoadProgressObserver { get; set; }

    internal Action<CaptureDecodeProgress>? CaptureDecodeProgressObserver { get; set; }

    private async void LoadButton_OnClick(object? sender, RoutedEventArgs e) =>
        await _replayShell.ExecuteAsync(_replayShell.LoadCommand);

    private ReplayShellViewModel CreateReplayShell() => new(_captureWorkspace, new ReplayShellSeams(
        PickCaptureSourceAsync,
        path => OpenCaptureAsync(path, promptForConfiguration: true),
        () => DecodeSelectedAsync(),
        CancelCaptureOperation,
        () => operationInProgress,
        () => outputExportJobs.IsActive));

    private async Task<string?> PickCaptureSourceAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load capture",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported captures") { Patterns = ["*.txt", "*.log", "*.csv", "*.xlsx", "*.xlsm"] },
                FilePickerFileTypes.All,
            ],
        });
        return files.SingleOrDefault()?.TryGetLocalPath();
    }

    private void CancelCaptureOperation()
    {
        operationCancellation?.Cancel();
        captureDecodeController.CancelCurrent();
    }

    internal async Task OpenCaptureAsync(
        string path,
        string? adapterId = null,
        bool promptForConfiguration = false)
    {
        HideRegisterProfileInference();
        var loadOperation = _captureWorkspace.BeginLoad();
        captureDecodeController.CancelCurrent();
        ResetOutputPlanning();
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        var loadCancellation = new CancellationTokenSource();
        operationCancellation = loadCancellation;
        var cancellationToken = loadCancellation.Token;
        SetBusy(true, "Opening capture");

        try
        {
            var progress = new InlineProgress<CaptureLoadProgress>(item =>
            {
                CaptureLoadProgressObserver?.Invoke(item);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested ||
                        !IsActiveCaptureLoad(loadOperation, loadCancellation))
                        return;
                    var count = item.RecordsRead > 0 ? $" · {item.RecordsRead:N0} records" : string.Empty;
                    SessionStatusText.Text = item.Phase + count;
                });
            });
            var nextSession = await Task.Run(
                () => CaptureSession.LoadAsync(path, progress, cancellationToken, adapterId),
                cancellationToken);
            var profileInference = await Task.Run(
                () => NvtRegisterProfileInference.Infer(nextSession.Records, targetI2cAddress),
                cancellationToken);
            if (profileInference.UniqueProfile is { } inferredProfile)
            {
                nextSession = await Task.Run(
                    () => nextSession.WithRegisterProfile(inferredProfile.IcFamily, cancellationToken),
                    cancellationToken);
            }
            var projectedActivities = await Task.Run(
                () => RegisterActivityProjector.Project(nextSession.Records, nextSession.RegisterAnnotations).ToArray(),
                cancellationToken);
            var nextDiagnostics = SessionDiagnostics(nextSession);
            var nextCapture = new CaptureData(nextSession,
                RawCaptureProjection.Create(nextSession, projectedActivities),
                new CaptureDiagnostics(nextDiagnostics));

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActiveCaptureLoad(loadOperation, loadCancellation))
                throw new OperationCanceledException(cancellationToken);
            ClearSession();
            if (!_captureWorkspace.AdoptProbed(loadOperation, nextCapture))
                throw new OperationCanceledException(cancellationToken);
            RefreshRawExplorer();
            CreateReviewWorkspace(nextDiagnostics, []);
            CaptureNameText.Text = Path.GetFileName(nextSession.SourcePath).ToUpperInvariant();
            SessionStatusText.Text = $"{nextSession.Records.Count:N0} physical records indexed · {diagnosticRows.Count:N0} source diagnostics · semantic format required";
            SourceAdapterText.Text = nextSession.Probe.DisplayName;
            Title = $"{ProductWindowTitle} — {nextSession.Probe.DisplayName}";
            SourceAdapterText.IsVisible = true;
            SourceAdapterComboBox.IsVisible = false;
            SourceConfidenceText.Text = $"{nextSession.Probe.Confidence} confidence · {nextSession.Probe.Reasons.FirstOrDefault()}";
            SourceHashText.Text = $"SHA-256\n{nextSession.SourceSha256}";
            EventVersionComboBox.IsEnabled = true;
            RegisterProfileComboBox.IsEnabled = true;
            ExportReadableLogButton.IsEnabled = true;
            SelectRegisterProfileChoice(nextSession.RegisterProfile);
            if (profileInference.UniqueProfile is { } uniqueProfile)
            {
                ConfigurationHintText.Text =
                    $"IC profile {uniqueProfile.IcFamily} inferred from verified Event Buffer address evidence; confirm Event Buffer Version to decode.";
                SessionStatusText.Text =
                    $"{nextSession.Records.Count:N0} physical records indexed · IC {uniqueProfile.IcFamily} inferred · source bytes unchanged";
            }
            else
            {
                ConfigurationHintText.Text = "Confirm the version to decode automatically; Event Buffer format is never inferred.";
            }
            SetTimelineCounts(nextSession.Records.Count, 0, diagnosticRows.Count);
            TimelineStatusText.Text = "Raw capture indexed · confirm Event Buffer Version to open Paint";
            WorkspaceTabs.SelectedIndex = 0;
            if (allRawRows.Count > 0)
            {
                RawRecordsList.SelectedIndex = 0;
            }
            if (promptForConfiguration ||
                profileInference.Status is NvtRegisterProfileInferenceStatus.Ambiguous or NvtRegisterProfileInferenceStatus.Conflicting)
                ShowRegisterProfileInference(profileInference);
        }
        catch (OperationCanceledException)
        {
            if (IsActiveCaptureLoad(loadOperation, loadCancellation))
                SessionStatusText.Text = "Load cancelled; no partial session was committed";
        }
        catch (SourceSelectionRequiredException exception)
        {
            if (IsActiveCaptureLoad(loadOperation, loadCancellation))
            {
                _captureWorkspace.RequireSourceChoice(loadOperation, new PendingCaptureSource(path, promptForConfiguration));
                using (configuringSourceChoice.Enter())
                {
                    SourceAdapterComboBox.ItemsSource = exception.Candidates
                        .Select(candidate => new SourceAdapterChoice(candidate.AdapterId, candidate.DisplayName, candidate.Confidence))
                        .ToArray();
                    ComboBoxAutoSizer.Fit(SourceAdapterComboBox);
                    SourceAdapterComboBox.SelectedIndex = -1;
                    SourceAdapterComboBox.IsVisible = true;
                    SourceAdapterText.IsVisible = false;
                }
                SessionStatusText.Text = "Source selection required; no adapter was chosen automatically";
                SourceConfidenceText.Text = "The highest-confidence probe is tied";
                ConfigurationHintText.Text = "Choose the correct source adapter; Event Buffer Version remains a separate decision.";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (IsActiveCaptureLoad(loadOperation, loadCancellation))
            {
                SessionStatusText.Text = $"Load failed · {exception.Message}";
                ConfigurationHintText.Text = "Choose another file, inspect its schema, or select a supported adapter explicitly.";
            }
        }
        finally
        {
            if (IsActiveCaptureLoad(loadOperation, loadCancellation))
            {
                _captureWorkspace.Fail(loadOperation);
                SetBusy(false, SessionStatusText.Text ?? "Ready");
            }
        }
    }

    internal async Task ApplyStartupDecodeAsync(string eventVersion, string? palmProfile, string? registerProfile = null)
    {
        HideRegisterProfileInference();
        if (!string.IsNullOrWhiteSpace(registerProfile))
        {
            var profileChoice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
                .FirstOrDefault(item => item.IcFamily?.Equals(registerProfile, StringComparison.OrdinalIgnoreCase) == true);
            if (profileChoice is null)
            {
                ConfigurationHintText.Text = $"Unsupported startup IC register profile: {registerProfile}";
                return;
            }
            using (configuringRegisterProfile.Enter())
            {
                RegisterProfileComboBox.SelectedItem = profileChoice;
            }
            await ApplyRegisterProfileAsync(profileChoice);
        }

        var normalizedVersion = eventVersion.Trim().ToUpperInvariant();
        if (!normalizedVersion.StartsWith("0X", StringComparison.Ordinal))
            normalizedVersion = $"0X{normalizedVersion}";

        var versionIndex = normalizedVersion switch
        {
            "0X82" => 0,
            "0X83" => 1,
            "0X84" => 2,
            "0X85" => 3,
            "0X97" => 4,
            _ => -1,
        };
        if (versionIndex < 0)
        {
            ConfigurationHintText.Text = $"Unsupported startup Event Buffer Version: {eventVersion}";
            return;
        }

        using (configuringEventVersion.Enter())
        {
            EventVersionComboBox.SelectedIndex = versionIndex;
        }
        if (versionIndex == 4)
        {
            if (string.IsNullOrWhiteSpace(registerProfile))
            {
                ConfigurationHintText.Text = "Startup 0x97 decode requires --register-profile and --palm-profile.";
                return;
            }
            var profileIndex = palmProfile?.Trim().ToUpperInvariant() switch
            {
                "STANDARD" => 0,
                "BENZ" or "BENZ PALM" or "BENZ-PALM" => 1,
                _ => -1,
            };
            if (profileIndex < 0)
            {
                ConfigurationHintText.Text = "Startup 0x97 decode requires --palm-profile Standard or Benz-Palm.";
                return;
            }
            Desay97ProfileComboBox.SelectedIndex = profileIndex;
        }

        await _replayShell.ExecuteAsync(_replayShell.DecodeCommand);
    }

    private async Task DecodeSelectedAsync(
        bool preserveWorkspaceContext = false,
        ReviewWorkspaceState? preservedWorkspaceStateOverride = null,
        TabItem? preservedWorkspaceTabOverride = null)
    {
        if (_captureWorkspace.State is CaptureState.Empty || operationInProgress)
        {
            return;
        }

        if (EventVersionComboBox.SelectedItem is not ComboBoxItem selected ||
            selected.Content is not string versionText)
        {
            ConfigurationHintText.Text = "Confirm Event Buffer Version; decoding starts immediately after selection.";
            return;
        }

        var loadedSession = _captureWorkspace.Capture!.Session;
        var isDesay97 = versionText.Equals("0x97", StringComparison.OrdinalIgnoreCase);
        var registerProfile = NvtRegisterCatalog.FindProfile(loadedSession.RegisterProfile);
        Desay97Profile? desayProfile = null;
        if (isDesay97)
        {
            if (registerProfile is null)
            {
                ConfigurationHintText.Text = "Desay 0x97 requires an explicit IC profile before decoding.";
                return;
            }
            desayProfile = Desay97ProfileComboBox.SelectedItem is ComboBoxItem profileItem &&
                           profileItem.Content?.ToString() == "Benz Palm"
                ? Desay97Profile.BenzPalm
                : Desay97ProfileComboBox.SelectedItem is ComboBoxItem
                    ? Desay97Profile.Standard
                    : null;
            if (desayProfile is null)
            {
                ConfigurationHintText.Text = "Desay 0x97 requires an explicit Standard or Benz Palm selection.";
                return;
            }
        }
        else if (!CommonEventBufferDecoder.TryParseVersion(versionText, out _))
        {
            ConfigurationHintText.Text = "The selected Event Buffer Version is not supported.";
            return;
        }

        var preservedWorkspaceState = preservedWorkspaceStateOverride ?? CaptureReviewWorkspaceState();
        var preservedWorkspaceTab = preservedWorkspaceTabOverride ?? WorkspaceTabs.SelectedItem as TabItem;
        var preservedPaintSettings = paintWorkspace?.Settings;

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        ResetOutputPlanning();
        var decodeCancellation = new CancellationTokenSource();
        operationCancellation = decodeCancellation;
        var cancellationToken = decodeCancellation.Token;
        SetBusy(true, $"Decoding {(isDesay97 ? "Desay" : "Common")} {versionText}");
        PendingCaptureOperation.Decode? decodeOperation = null;

        try
        {
            var request = new PreparedCaptureDecodeRequest(
                loadedSession,
                new FormatDecodeRequest(
                    versionText,
                    loadedSession.RegisterProfile,
                    isDesay97
                        ? desayProfile == Desay97Profile.BenzPalm ? "benz-palm" : "standard"
                        : null,
                    targetI2cAddress));
            var progress = new InlineProgress<CaptureDecodeProgress>(item =>
            {
                CaptureDecodeProgressObserver?.Invoke(item);
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(operationCancellation, decodeCancellation))
                        PresentCaptureDecodeProgress(item);
                });
            });
            var operation = captureDecodeController.StartPrepared(request, progress, cancellationToken);
            decodeOperation = _captureWorkspace.BeginDecode(operation);
            var result = await operation.Completion;
            var presentation = await Task.Run(
                () => BuildCaptureDecodePresentation(result, cancellationToken),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!_captureWorkspace.IsCurrent(decodeOperation) ||
                !ReferenceEquals(operationCancellation, decodeCancellation) ||
                !ReferenceEquals(captureDecodeController.LastSuccessfulResult, result) ||
                !ReferenceEquals(session, loadedSession))
                throw new CaptureDecodeSupersededException(result.Generation);

            var nextConfiguration = result.Decode.Configuration;

            cancellationToken.ThrowIfCancellationRequested();
            var preserveReview = preserveWorkspaceContext || decodeConfiguration is null || decodeConfiguration == nextConfiguration;
            var preservedMarkers = preserveReview ? reviewWorkspace?.Markers.ToArray() ?? [] : [];
            var preservedReviewState = preserveReview ? reviewWorkspace?.ReviewSession.ExportState() ?? [] : [];
            ResetOutputPlanning();
            if (!_captureWorkspace.AdoptDecoded(decodeOperation, presentation.Projection))
                throw new CaptureDecodeSupersededException(result.Generation);
            autoPauseIndex = result.Workspace.AutoPauseIndex;
            CreateReviewWorkspace(
                diagnosticRows.Select(row => row.Diagnostic).ToArray(),
                result.Workspace.Frames.Snapshots,
                preservedMarkers,
                preservedReviewState,
                preserveReview ? preservedWorkspaceState : null);
            paintWorkspace = new ReplayPaintWorkspace(
                presentation.Replay,
                presentation.TrailHistory,
                CreateInitialPaintSettings(presentation.Extent, preservedPaintSettings, presentation.Replay.Count),
                reviewWorkspace?.ReviewSession.Diagnostics,
                reviewWorkspace?.Markers);

            DecodedFramesList.ItemsSource = decodedRows;
            SessionStatusText.Text = $"{result.Decode.DisplayIdentity} · I²C 0x{targetI2cAddress:X2} · {decodedRows.Count:N0} frames · {diagnosticRows.Count:N0} findings";
            ConfigurationHintText.Text = $"{result.Decode.DisplayIdentity} confirmed · 7-bit I²C 0x{targetI2cAddress:X2} · decoded automatically · raw source remains unchanged";
            SetTimelineCounts(result.Capture.Records.Count, decodedRows.Count, diagnosticRows.Count);
            TimelineStatusText.Text = decodedRows.Count > 0
                ? "Logical replay ready · Space play/pause · ←/→ step · drag Loop handles"
                : "No replayable event-buffer frames · inspect physical records and Review Queue";
            InitializeReplay();
            SaveReviewButton.IsEnabled = true;
            LoadReviewButton.IsEnabled = true;
            AnalysisTab.IsEnabled = true;
            AnalysisSummaryText.Text = $"Ready · {decodedRows.Count:N0} frames · export the full replay or current In/Out range";
            AddMarkerButton.IsEnabled = decodedRows.Count > 0;
            if (decodedRows.Count > 0)
            {
                var logicalIndex = preserveWorkspaceContext
                    ? Math.Clamp(preservedWorkspaceState?.CurrentLogicalIndex ?? 0, 0, decodedRows.Count - 1)
                    : 0;
                if (preserveWorkspaceContext && preservedWorkspaceTab?.IsEnabled == true)
                    WorkspaceTabs.SelectedItem = preservedWorkspaceTab;
                else
                    WorkspaceTabs.SelectedItem = PaintTab;
                outputWorkspaceActive = ReferenceEquals(WorkspaceTabs.SelectedItem, AnalysisTab);
                var paintSelected = ReferenceEquals(WorkspaceTabs.SelectedItem, PaintTab);
                SetOutputWorkspaceChrome(outputWorkspaceActive, showReplayTransport: paintSelected);
                SetSourceTransportEnabled(paintSelected && !outputWorkspaceActive);
                SeekReplay(logicalIndex);
                if (preserveWorkspaceContext && preservedWorkspaceState?.SelectedContactId is { } contactId)
                {
                    reviewWorkspace?.SelectContact(contactId);
                    if (replayFrames is { } frames) PresentReplayDetails(frames[logicalIndex]);
                }
                RefreshOutputPreviewIfVisible();
            }
            else
            {
                WorkspaceTabs.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
            if (IsActiveCaptureDecode(decodeOperation, decodeCancellation))
                SessionStatusText.Text = "Decode cancelled; previous complete result was preserved";
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException)
        {
            if (IsActiveCaptureDecode(decodeOperation, decodeCancellation))
            {
                SessionStatusText.Text = $"Decode failed · {exception.Message}";
                ConfigurationHintText.Text = "Raw records remain available; verify version/profile and try again.";
            }
        }
        finally
        {
            if (IsActiveCaptureDecode(decodeOperation, decodeCancellation))
            {
                if (decodeOperation is not null) _captureWorkspace.Fail(decodeOperation);
                SetBusy(false, SessionStatusText.Text ?? "Ready");
            }
        }
    }

    private bool IsActiveCaptureLoad(PendingCaptureOperation.Load pending, CancellationTokenSource cancellation) =>
        _captureWorkspace.IsCurrent(pending) && ReferenceEquals(operationCancellation, cancellation);

    private bool IsActiveCaptureDecode(PendingCaptureOperation.Decode? pending, CancellationTokenSource cancellation) =>
        ReferenceEquals(operationCancellation, cancellation) &&
        (pending is null || _captureWorkspace.IsCurrent(pending));

    private void CancelButton_OnClick(object? sender, RoutedEventArgs e) => _replayShell.CancelCommand.Execute(null);

    private async void EventVersionComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var comboBox = sender as ComboBox;
        var isDesay97 = comboBox?.SelectedItem is ComboBoxItem item &&
                        item.Content?.ToString() == "0x97";
        if (Desay97ProfileComboBox is null)
        {
            return;
        }

        Desay97ProfileComboBox.IsVisible = isDesay97;
        if (!isDesay97)
        {
            Desay97ProfileComboBox.SelectedIndex = -1;
        }
        if (session is not null)
        {
            ConfigurationHintText.Text = isDesay97
                ? NvtRegisterCatalog.FindProfile(session.RegisterProfile) is null
                    ? $"0x97 pending · {ActiveDecodeContext()} · select IC profile, then Standard or Benz Palm."
                    : $"0x97 pending · {ActiveDecodeContext()} · select Standard or Benz Palm."
                : "Version confirmed · decoding automatically; source detection did not infer it.";
        }
        if (!configuringEventVersion.IsActive && session is not null && !isDesay97 &&
            comboBox is { SelectedIndex: >= 0, IsDropDownOpen: false } &&
            !SelectedDecodeConfigurationMatchesActive())
            await _replayShell.ExecuteAsync(_replayShell.DecodeCommand);
    }

    private void Desay97ProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (session is not null && Desay97ProfileComboBox.SelectedIndex >= 0)
            ConfigurationHintText.Text = $"Palm profile selected · {ActiveDecodeContext()} · close the menu to decode.";
    }

    private async void EventVersionComboBox_OnDropDownClosed(object? sender, EventArgs e)
    {
        var isDesay97 = EventVersionComboBox.SelectedItem is ComboBoxItem versionItem &&
                        versionItem.Content?.ToString() == "0x97";
        if (session is not null && !isDesay97 && EventVersionComboBox.SelectedIndex >= 0 &&
            !SelectedDecodeConfigurationMatchesActive())
            await _replayShell.ExecuteAsync(_replayShell.DecodeCommand);
    }

    private async void Desay97ProfileComboBox_OnDropDownClosed(object? sender, EventArgs e)
    {
        var isDesay97 = EventVersionComboBox.SelectedItem is ComboBoxItem versionItem &&
                        versionItem.Content?.ToString() == "0x97";
        if (session is not null && isDesay97 && Desay97ProfileComboBox.SelectedIndex >= 0 &&
            NvtRegisterCatalog.FindProfile(session.RegisterProfile) is not null &&
            !SelectedDecodeConfigurationMatchesActive())
            await _replayShell.ExecuteAsync(_replayShell.DecodeCommand);
        else if (session is not null && isDesay97 && Desay97ProfileComboBox.SelectedIndex >= 0)
            ConfigurationHintText.Text = $"Palm profile confirmed · {ActiveDecodeContext()} · select the IC profile to decode 0x97.";
    }

    private bool SelectedDecodeConfigurationMatchesActive()
    {
        if (_captureWorkspace.State is not CaptureState.Decoded active ||
            EventVersionComboBox.SelectedItem is not ComboBoxItem versionItem)
            return false;

        var decodeConfiguration = active.Replay.Configuration;
        var session = active.Capture.Session;
        var version = versionItem.Content?.ToString() ?? string.Empty;
        var palmProfile = version.Equals("0x97", StringComparison.OrdinalIgnoreCase)
            ? Desay97ProfileComboBox.SelectedItem is ComboBoxItem palmItem
                ? palmItem.Content?.ToString()
                : null
            : null;
        var registerProfile = NvtRegisterCatalog.FindProfile(session.RegisterProfile);
        var eventBufferBase = registerProfile?.EventBufferBase ?? 0;
        return decodeConfiguration.EventBufferVersion.Equals(version, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(decodeConfiguration.Desay97Profile, palmProfile, StringComparison.OrdinalIgnoreCase) &&
               decodeConfiguration.SourceAdapterId.Equals(session.Probe.AdapterId, StringComparison.Ordinal) &&
               decodeConfiguration.EventBufferBase == eventBufferBase &&
               string.Equals(decodeConfiguration.RegisterProfile, session.RegisterProfile, StringComparison.OrdinalIgnoreCase) &&
               decodeConfiguration.TargetI2cAddress == targetI2cAddress;
    }

    private string ActiveDecodeContext()
    {
        if (decodeConfiguration is null) return "no active replay";
        var format = decodeConfiguration.EventBufferVersion.Equals("0x97", StringComparison.OrdinalIgnoreCase)
            ? $"Desay 0x97 / {decodeConfiguration.Desay97Profile ?? "unconfirmed Palm"}"
            : $"Common {decodeConfiguration.EventBufferVersion}";
        return $"active replay: {format} · I²C 0x{decodeConfiguration.TargetI2cAddress:X2}";
    }

    private async void RegisterProfileComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringRegisterProfile.IsActive || session is null ||
            RegisterProfileComboBox.SelectedItem is not RegisterProfileChoice choice)
            return;

        HideRegisterProfileInference();
        await ApplyRegisterProfileAsync(choice);
    }

    private void SelectRegisterProfileChoice(string? icFamily)
    {
        var choices = RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>;
        var choice = choices?.FirstOrDefault(item =>
            string.Equals(item.IcFamily, icFamily, StringComparison.OrdinalIgnoreCase));
        if (choice is null && icFamily is null) choice = choices?.FirstOrDefault(item => item.IcFamily is null);
        using (configuringRegisterProfile.Enter())
        {
            RegisterProfileComboBox.SelectedItem = choice;
        }
    }

    internal NvtRegisterProfileInferenceResult? PendingRegisterProfileInferenceForTesting => pendingRegisterProfileInference;

    internal async Task ResolveRegisterProfileInferenceForTestingAsync(string icFamily)
    {
        if (pendingRegisterProfileInference is null)
            throw new InvalidOperationException("No IC profile inference is awaiting confirmation.");
        var choice = (InferredRegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
            .SingleOrDefault(item => item.IcFamily == icFamily)
            ?? throw new ArgumentException($"IC profile '{icFamily}' is not a current inference candidate.", nameof(icFamily));
        HideRegisterProfileInference();
        SelectRegisterProfileChoice(choice.IcFamily);
        await ApplyRegisterProfileAsync(choice);
    }

    private async Task ApplyRegisterProfileAsync(RegisterProfileChoice choice, bool decodeIfReady = true)
    {
        if (session is null) return;
        var selectedId = (RawRecordsList.SelectedItem as RawRecordRow)?.Record.StableId;
        var loadedSession = session;
        var operationIdentity = CaptureReviewOperationIdentity();
        var preservedWorkspaceState = CaptureReviewWorkspaceState();
        var preservedWorkspaceTab = WorkspaceTabs.SelectedItem as TabItem;
        CaptureSession? profiledSession = null;
        var committed = false;
        SetBusy(true, choice.IcFamily is null ? "Removing IC register profile" : $"Applying IC profile {choice.IcFamily}");
        try
        {
            profiledSession = await Task.Run(() => loadedSession.WithRegisterProfile(choice.IcFamily));
            var projected = await Task.Run(() =>
                RegisterActivityProjector.Project(profiledSession.Records, profiledSession.RegisterAnnotations).ToArray());
            if (!IsCurrentReviewOperation(operationIdentity)) return;

            var diagnostics = SessionDiagnostics(profiledSession);
            CollapseExpandedRawRow();
            _captureWorkspace.UpdateProfile(new CaptureData(profiledSession,
                RawCaptureProjection.Create(profiledSession, projected),
                new CaptureDiagnostics(diagnostics), reviewWorkspace));
            RefreshRawExplorer(selectedId);
            CreateReviewWorkspace(diagnostics, workspaceState: preservedWorkspaceState);
            SynchronizeReviewWorkspaceConsumers();
            committed = true;
            var ambiguous = registerActivities.Count(item => item.IsAmbiguous);
            ConfigurationHintText.Text = choice.IcFamily is null
                ? $"IC profile unconfirmed · {ambiguous:N0} collision-prone register events remain raw-only"
                : $"IC profile {choice.IcFamily} confirmed · register semantics and Event Buffer base reapplied";
            SessionStatusText.Text = choice.IcFamily is null
                ? "Register profile removed; original capture remains unchanged"
                : $"Register profile {choice.IcFamily} applied without changing source bytes";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            SessionStatusText.Text = $"Register profile failed · {exception.Message}";
        }
        finally
        {
            var stillOwnsUi = committed
                ? ReferenceEquals(session, profiledSession) &&
                  ReferenceEquals(_captureWorkspace.Pending, operationIdentity.Pending)
                : IsCurrentReviewOperation(operationIdentity);
            if (stillOwnsUi) SetBusy(false, SessionStatusText.Text ?? "Ready");
        }

        if (!committed) return;
        var canDecode = EventVersionComboBox.SelectedIndex >= 0 &&
            (EventVersionComboBox.SelectedIndex != 4 ||
             (choice.IcFamily is not null && Desay97ProfileComboBox.SelectedIndex >= 0));
        if (decodeIfReady && canDecode)
            await DecodeSelectedAsync(
                preserveWorkspaceContext: true,
                preservedWorkspaceStateOverride: preservedWorkspaceState,
                preservedWorkspaceTabOverride: preservedWorkspaceTab);
    }

    internal async Task ApplyRegisterProfileForTestingAsync(string? icFamily)
    {
        var choice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
            .FirstOrDefault(item => string.Equals(item.IcFamily, icFamily, StringComparison.OrdinalIgnoreCase));
        if (choice is null && icFamily is null)
            choice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
                .FirstOrDefault(item => item.IcFamily is null);
        if (choice is null) throw new ArgumentException($"Unknown IC profile '{icFamily}'.", nameof(icFamily));
        await ApplyRegisterProfileAsync(choice);
    }

    private void RegisterFilterComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => RefreshRawExplorer();

    private void RegisterSearchTextBox_OnTextChanged(object? sender, TextChangedEventArgs e) => RefreshRawExplorer();

    private async void TargetI2cAddressTextBox_OnLostFocus(object? sender, RoutedEventArgs e) =>
        await CommitTargetI2cAddressAsync();

    private async void TargetI2cAddressTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            TargetI2cAddressTextBox.Text = FormatTargetI2cAddress(targetI2cAddress);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;
        await CommitTargetI2cAddressAsync();
        e.Handled = true;
    }

    private async Task CommitTargetI2cAddressAsync()
    {
        if (!TryParseTargetI2cAddress(TargetI2cAddressTextBox.Text, out var requested))
        {
            TargetI2cAddressTextBox.Text = FormatTargetI2cAddress(targetI2cAddress);
            SessionStatusText.Text = "I²C address rejected · enter a 7-bit value from 0x00 through 0x7F";
            return;
        }

        TargetI2cAddressTextBox.Text = FormatTargetI2cAddress(requested);
        if (requested == targetI2cAddress) return;
        targetI2cAddress = requested;
        if (session is null)
        {
            SessionStatusText.Text = $"Decode target set to 7-bit I²C address 0x{targetI2cAddress:X2}";
            return;
        }

        HideRegisterProfileInference();
        var inference = NvtRegisterProfileInference.Infer(session.Records, targetI2cAddress);
        var icFamily = inference.UniqueProfile?.IcFamily;
        var choice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
            .FirstOrDefault(item => string.Equals(item.IcFamily, icFamily, StringComparison.OrdinalIgnoreCase));
        if (choice is null && icFamily is null)
        {
            choice = (RegisterProfileComboBox.ItemsSource as IEnumerable<RegisterProfileChoice>)?
                .FirstOrDefault(item => item.IcFamily is null);
        }
        if (choice is null)
            throw new InvalidOperationException($"IC profile choice '{icFamily ?? "Unconfirmed"}' is unavailable.");

        SelectRegisterProfileChoice(choice.IcFamily);
        await ApplyRegisterProfileAsync(choice);
        if (inference.Status is NvtRegisterProfileInferenceStatus.Ambiguous or NvtRegisterProfileInferenceStatus.Conflicting)
            ShowRegisterProfileInference(inference);
        else if (inference.Status == NvtRegisterProfileInferenceStatus.None)
            ConfigurationHintText.Text = $"I²C 0x{targetI2cAddress:X2} selected · no complete Event Buffer address evidence; IC profile remains unconfirmed.";
    }

    internal Task SetTargetI2cAddressForTestingAsync(string value)
    {
        TargetI2cAddressTextBox.Text = value;
        return CommitTargetI2cAddressAsync();
    }

    internal static bool TryParseTargetI2cAddress(string? value, out int address)
    {
        var text = value?.Trim() ?? string.Empty;
        var style = NumberStyles.Integer;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
            style = NumberStyles.AllowHexSpecifier;
        }
        return int.TryParse(text, style, CultureInfo.InvariantCulture, out address) &&
               address is >= 0 and <= 0x7F;
    }

    private static string FormatTargetI2cAddress(int address) => $"0x{address:X2}";

    private void RefreshRawExplorer(string? preferredStableId = null)
    {
        if (RawRecordsList is null || RegisterActivitySurface is null) return;
        preferredStableId ??= (RawRecordsList.SelectedItem as RawRecordRow)?.Record.StableId;
        var filter = (RegisterFilterComboBox.SelectedItem as RawRegisterFilterChoice)?.Filter ?? RawRegisterFilter.All;
        var query = RegisterSearchTextBox.Text ?? string.Empty;
        var visible = allRawRows.Where(row => row.Matches(query) && filter switch
        {
            RawRegisterFilter.All => true,
            RawRegisterFilter.Registers => row.Activity is not null,
            RawRegisterFilter.ChangedReads => row.Activity?.ChangedFromPreviousSample == true,
            RawRegisterFilter.WritesAndCommands => row.Activity?.Kind is RegisterActivityKind.Write or RegisterActivityKind.Command or RegisterActivityKind.Reset or RegisterActivityKind.PageSwitch,
            RawRegisterFilter.Ambiguous => row.Activity?.IsAmbiguous == true,
            _ => true,
        }).ToArray();
        if (expandedRawRow is not null && !visible.Contains(expandedRawRow)) CollapseExpandedRawRow();
        RawRecordsList.ItemsSource = visible;
        var visibleIds = visible.Select(row => row.Record.StableId).ToHashSet(StringComparer.Ordinal);
        var visibleActivities = registerActivities.Where(item => visibleIds.Contains(item.Record.StableId)).ToArray();
        var minimum = allRawRows.Count == 0 ? 0 : allRawRows.Min(row => row.Record.Index);
        var maximum = allRawRows.Count == 0 ? 1 : allRawRows.Max(row => row.Record.Index);
        RegisterActivitySurface.SetActivities(visibleActivities, minimum, maximum);
        RegisterActivitySurface.IsEnabled = visibleActivities.Length > 0;
        RegisterResultText.Text = $"{visible.Length:N0} records · {visibleActivities.Length:N0} register events" +
            (visibleActivities.Any(item => item.IsAmbiguous) ? $" · {visibleActivities.Count(item => item.IsAmbiguous):N0} ambiguous" : string.Empty);
        if (preferredStableId is not null && visible.FirstOrDefault(row => row.Record.StableId == preferredStableId) is { } preferred)
            RawRecordsList.SelectedItem = preferred;
        else if (visible.Length > 0)
            RawRecordsList.SelectedItem = visible[0];
        else
        {
            RawRecordsList.SelectedItem = null;
            RegisterActivitySurface.SetSelected(null);
            InspectorTitleText.Text = "No matching record";
            InspectorSubtitleText.Text = "Adjust the Raw Explorer search or register filter.";
            InspectorLogicalText.Text = "-";
            InspectorCrcText.Text = "-";
            InspectorAsilText.Text = "-";
            InspectorAllBreakText.Text = "ALL BREAK";
            InspectorAllBreakBadge.IsVisible = false;
            ProtocolFieldsItemsControl.ItemsSource = null;
            TransportFieldsItemsControl.ItemsSource = null;
        }
    }

    private void RegisterActivitySurface_OnActivitySelected(object? sender, RegisterActivitySelectedEventArgs e)
    {
        if (!rawRowsById.TryGetValue(e.Record.StableId, out var row)) return;
        RawRecordsList.SelectedItem = row;
        RawRecordsList.ScrollIntoView(row);
    }

    private async void ExportReadableLogButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export readable communication log",
            AllowMultiple = false,
        });
        var directory = folders.SingleOrDefault()?.TryGetLocalPath();
        if (directory is null) return;

        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        var cancellationToken = operationCancellation.Token;
        SetBusy(true, "Exporting readable communication log");
        try
        {
            var result = await new ReadableCommunicationLogWriter().WriteAsync(
                directory,
                session.Records,
                session.RegisterAnnotations,
                cancellationToken);
            SessionStatusText.Text =
                $"Readable log exported · {result.RecordCount:N0} records · {result.AnnotatedRegisterCount:N0} annotated · {result.AmbiguousRegisterCount:N0} ambiguous";
        }
        catch (OperationCanceledException)
        {
            SessionStatusText.Text = "Readable log export cancelled; prior complete output was preserved";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SessionStatusText.Text = $"Readable log export failed · {exception.Message}";
        }
        finally
        {
            SetBusy(false, SessionStatusText.Text ?? "Ready");
        }
    }

    private async void SourceAdapterComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (configuringSourceChoice.IsActive || _captureWorkspace.PendingSource is not { } source ||
            SourceAdapterComboBox.SelectedItem is not SourceAdapterChoice choice)
            return;
        await OpenCaptureAsync(
            source.Path,
            choice.AdapterId,
            promptForConfiguration: source.RequiresConfiguration);
    }

    private static ReplayDiagnostic[] SessionDiagnostics(CaptureSession capture) =>
        capture.TransportDiagnostics.Concat(capture.RegisterDiagnostics).ToArray();

    private sealed record SourceAdapterChoice(string AdapterId, string DisplayName, ProbeConfidence Confidence)
    {
        public override string ToString() => $"{DisplayName} · {Confidence}";
    }

    private void ClearSession()
    {
        HideRegisterProfileInference();
        StopPlayback();
        ResetOutputPlanning();
        _captureWorkspace.Clear();
        paintWorkspace = null;
        autoPauseIndex = null;
        CollapseExpandedRawRow();
        reviewRows = [];
        ReplayTimelineSurface.SetMarkerFrames([]);
        pendingSidecar = null;
        playbackController.Clear();
        currentInspectorRecord = null;
        currentInspectorFrame = null;
        currentInspectorSnapshot = null;
        currentInspectorPresentation = null;
        SourceLineText.Text = "-";
        SourceLineButton.IsEnabled = false;
        SourceOffsetText.Text = "-";
        StableIdText.Text = "-";
        lastDetailPresentationTimestamp = 0;
        DetailPresentationCount = 0;
        RawRecordsList.ItemsSource = null;
        RegisterSearchTextBox.Text = string.Empty;
        TargetI2cAddressTextBox.Text = FormatTargetI2cAddress(targetI2cAddress);
        RegisterFilterComboBox.SelectedIndex = 0;
        using (configuringRegisterProfile.Enter())
        {
            RegisterProfileComboBox.SelectedIndex = 0;
        }
        RegisterProfileComboBox.IsEnabled = false;
        ExportReadableLogButton.IsEnabled = false;
        RegisterActivitySurface.IsEnabled = false;
        RegisterActivitySurface.SetActivities([], 0, 1);
        RegisterActivitySurface.SetSelected(null);
        RegisterResultText.Text = "0 records · 0 register events";
        DecodedFramesList.ItemsSource = null;
        DiagnosticListBox.ItemsSource = null;
        ReviewOccurrenceComboBox.ItemsSource = null;
        ReviewActionsPanel.IsVisible = false;
        ReviewEmptyText.IsVisible = true;
        AnnotationMetadataPanel.IsVisible = false;
        MarkerQaCaseTextBox.Text = string.Empty;
        EventVersionComboBox.SelectedIndex = -1;
        EventVersionComboBox.IsEnabled = false;
        Desay97ProfileComboBox.SelectedIndex = -1;
        Desay97ProfileComboBox.IsVisible = false;
        PaintTab.IsEnabled = false;
        PreviousFrameButton.IsEnabled = false;
        PlayPauseButton.IsEnabled = false;
        NextFrameButton.IsEnabled = false;
        LoopToggleButton.IsEnabled = false;
        LoopToggleButton.IsChecked = false;
        AddMarkerButton.IsEnabled = false;
        SaveReviewButton.IsEnabled = false;
        LoadReviewButton.IsEnabled = false;
        ExportSelectedOutputButton.IsEnabled = false;
        AnalysisTab.IsEnabled = false;
        currentOutputReport = null;
        outputRangeUserDefined = false;
        InitializeOutputRange();
        ClearOutputVideoPreview();
        AnalysisHeatmapPreview.Show(null);
        AnalysisPointPlotPreview.Show(null, outputWorkspace.Settings.Width, outputWorkspace.Settings.Height);
        AnalysisSummaryText.Text = "Decode a capture to prepare output preview.";
        OutputRangeText.Text = "-";
        OutputClockText.Text = "-";
        OutputFindingsText.Text = "-";
        OutputHotspotText.Text = "Waiting for preview";
        OutputPointPlotText.Text = "Waiting for preview";
        PointPlotRangeText.Text = "Selected output range";
        OutputConfigurationText.Text = "Decoder configuration will appear here.";
        OutputSourceText.Text = "Source identity will appear here.";
        AnalysisOutputText.Text = "Nothing exported in this session.";
        OutputInfoFormatText.Text = "MP4 · H.264";
        OutputInfoSizeText.Text = "Waiting for preview";
        OutputInfoDetailText.Text = "120 FPS · 0 frames · 00:00.000 · range -";
        OutputContentComboBox.SelectedIndex = 0;
        LoadReviewButton.IsVisible = true;
        ApplySidecarButton.IsVisible = false;
        ReplayTimelineSurface.IsEnabled = false;
        ReplayTimelineSurface.SetMaximum(1);
        ReplayTimelineSurface.SetPosition(0);
        ReplayTimelineSurface.SetSupportingProgress(0, 0);
        ReplayTimelineSurface.SetLoopRange(null, null, false);
        ReplayClockText.Text = "00:00.000";
        ReplayEndClockText.Text = "00:00.000";
        LoopRangeText.Text = "Loop: off";
        PaintSurface.Fit();
        PaintZoomText.Text = "100%";
        PaintZoomHintBorder.IsVisible = false;
        using (synchronizingPaintControls.Enter())
        {
            PaintModeComboBox.SelectedIndex = 0;
            TrailModeComboBox.SelectedIndex = 1;
            TrailLengthComboBox.SelectedIndex = 5;
            TrailLengthComboBox.IsVisible = true;
            TrailLengthLabel.IsVisible = true;
            TraceToggleButton.IsChecked = true;
            TrailPointsToggleButton.IsChecked = true;
            GridStrengthToggleButton.IsChecked = false;
            ReverseXToggleButton.IsChecked = false;
            ReverseYToggleButton.IsChecked = false;
            SwapAxesToggleButton.IsChecked = false;
            LegendPositionComboBox.SelectedIndex = 0;
            LegendVisibleToggleButton.IsChecked = true;
            LegendCompactToggleButton.IsChecked = true;
            PaintSurface.SetMode(ReplayRenderMode.HostState);
            PaintSurface.SetTrailVisibility(showLines: true, showPoints: true);
            PaintSurface.SetStrongGrid(false);
            PaintSurface.SetLegendPosition(ReplayLegendPosition.TopLeft);
            PaintSurface.SetLegendVisible(true);
            PaintSurface.SetLegendCollapsed(true);
        }
        PaintSurface.Clear();
        DiagnosticCountText.Text = "0";
        InspectorTitleText.Text = "Frame";
        InspectorSubtitleText.Text = "Select a physical record or decoded event.";
        InspectorLogicalText.Text = "-";
        InspectorTimestampText.Text = "-";
        InspectorFingerText.Text = "0";
        InspectorGloveText.Text = "0";
        InspectorPalmText.Text = "0";
        InspectorContactCountText.Text = "0 active";
        InspectorContactsList.ItemsSource = null;
        InspectorContactsList.SelectedItem = null;
        InspectorContactsList.IsVisible = false;
        InspectorNoContactsText.IsVisible = false;
        ProtocolFieldsItemsControl.ItemsSource = null;
        TransportFieldsItemsControl.ItemsSource = null;
        InspectorCrcText.Text = "-";
        InspectorAsilText.Text = "-";
        InspectorAsilRawText.Text = string.Empty;
        InspectorAllBreakText.Text = "ALL BREAK";
        InspectorAllBreakBadge.IsVisible = false;
        SetHealthText(InspectorCrcText, false, false);
        SetHealthText(InspectorAsilText, false, false);
        CollapsedFingerText.Text = "0";
        CollapsedGloveText.Text = "0";
        CollapsedPalmText.Text = "0";
        CollapsedAsilText.Text = "ASIL -";
        SetHealthState(CollapsedAsilBadge, false, false);
        InspectorAlertBorder.IsVisible = false;
        InspectorAlertText.Text = string.Empty;
        SourceAdapterText.Text = "Probing…";
        Title = ProductWindowTitle;
        SourceConfidenceText.Text = "Source and Event Buffer format remain separate";
        SourceHashText.Text = "SHA-256 appears after loading";
        SourceFieldsText.Text = "No adapter-specific source fields.";
        SetTimelineCounts(0, 0, 0);
    }

    private static CaptureDecodePresentation BuildCaptureDecodePresentation(
        CaptureDecodeResult result,
        CancellationToken cancellationToken)
    {
        return new CaptureDecodePresentation(DecodedCaptureProjection.Create(result, cancellationToken));
    }

    private void PresentCaptureDecodeProgress(CaptureDecodeProgress progress)
    {
        if (_captureWorkspace.Pending is not PendingCaptureOperation.Decode pending ||
            progress.Generation != pending.Operation.Generation) return;
        var phase = progress.Phase switch
        {
            CaptureDecodePhase.SelectingFormat => "Selecting Event Buffer format",
            CaptureDecodePhase.ProjectingRegisters => "Applying IC register profile",
            CaptureDecodePhase.DecodingFrames => "Decoding Event Buffer frames",
            CaptureDecodePhase.BuildingWorkspace => "Building replay workspace",
            CaptureDecodePhase.Ready => "Replay workspace ready",
            _ => progress.Phase.ToString(),
        };
        SessionStatusText.Text = progress.TotalItems is > 0
            ? $"{phase} · {progress.CompletedItems:N0}/{progress.TotalItems:N0}"
            : phase;
    }

    private sealed record CaptureDecodePresentation(DecodedCaptureProjection Projection)
    {
        public ITouchReplaySession Replay => Projection.Session;
        public ReplayExtent Extent => Projection.Extent;
        public ReplayTrailHistory TrailHistory => Projection.TrailHistory;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string FormatBytes(IReadOnlyList<byte> data)
    {
        const int displayLimit = 4096;
        var count = Math.Min(data.Count, displayLimit);
        var builder = new StringBuilder(count * 3);
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(index % 16 == 0 ? Environment.NewLine : ' ');
            }

            builder.Append(data[index].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (data.Count > displayLimit)
        {
            builder.AppendLine();
            builder.Append($"… {data.Count - displayLimit:N0} additional bytes retained in the session");
        }

        return builder.ToString();
    }

    private static string FormatDecodedFields(CommonEventBufferFrame frame)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"touches       {frame.NumTouches}");
        builder.AppendLine($"all break     {frame.AllBreak}");
        builder.AppendLine($"crc           {(frame.CrcValid ? "OK" : $"FAIL ({frame.CapturedCrc:X2}/{frame.ComputedCrc:X2})")}");
        builder.AppendLine($"ASIL byte     0x{frame.Asil.Raw:X2}");
        builder.AppendLine($"button valid  {frame.Button.Valid}");
        if (frame.BusStatus is { } bus)
        {
            builder.AppendLine($"bus counters  no-response={bus.NoResponseCounter} busy={bus.BusyCounter}");
        }

        if (frame.DiagnosticPacket is { } packet)
        {
            builder.AppendLine($"EMS bitmap    {Convert.ToString(packet.EmsBitmap, 2).PadLeft(4, '0')}");
            builder.AppendLine($"global palm   {packet.PalmOn}");
        }

        if (frame.Fingers.Any(IsReportedFinger))
            builder.AppendLine("\nCONTACTS      TYPE     STATE        X     Y");
        foreach (var finger in frame.Fingers.Where(IsReportedFinger))
        {
            builder.AppendLine($"#{finger.Id,-2}           {finger.Type,-8} {finger.Status,-8} {finger.X,5} {finger.Y,5}");
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatDecodedFields(Desay97Frame frame)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"profile       {ProfileText(frame.Profile)}");
        builder.AppendLine($"touches       {frame.NumTouches}");
        builder.AppendLine($"all break     {frame.AllBreak}");
        builder.AppendLine($"crc           {(frame.CrcValid ? "OK" : $"FAIL ({frame.CapturedCrc:X2}/{frame.ComputedCrc:X2})")}");
        builder.AppendLine($"short/open    {frame.Short}/{frame.Open}");
        builder.AppendLine($"TP ASIL       {frame.TpAsilError}");
        builder.AppendLine($"phase 1       Physical #{frame.Packet.Probe.Index} · L{frame.Packet.Probe.Location.LineNumber}");
        builder.AppendLine($"phase 2       Physical #{frame.Packet.PayloadRead.Index} · L{frame.Packet.PayloadRead.Location.LineNumber}");
        if (frame.Fingers.Count > 0)
            builder.AppendLine("\nCONTACTS      TYPE     STATE        X     Y");
        foreach (var finger in frame.Fingers)
        {
            var semantic = finger.Invalid ? "Invalid" : finger.Palm ? "Palm" : "Finger";
            builder.AppendLine($"#{finger.Id,-2}           {semantic,-8} {finger.Status,-8} {finger.X,5} {finger.Y,5}");
        }
        return builder.ToString().TrimEnd();
    }

    private static bool IsReportedFinger(CommonFinger finger) =>
        finger.IsReported;

    private static string VersionText(CommonEventBufferVersion version) => version switch
    {
        CommonEventBufferVersion.V82 => "0x82",
        CommonEventBufferVersion.V83 => "0x83",
        CommonEventBufferVersion.V84 => "0x84",
        CommonEventBufferVersion.V85 => "0x85",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static string ProfileText(Desay97Profile profile) => profile switch
    {
        Desay97Profile.Standard => "Standard",
        Desay97Profile.BenzPalm => "Benz Palm",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
