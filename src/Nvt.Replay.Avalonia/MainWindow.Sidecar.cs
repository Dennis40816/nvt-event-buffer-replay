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
    private PendingReviewSidecar? pendingSidecar;
    private async void AttachMarkerEvidenceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await AttachSelectedMarkerEvidenceAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach raw Kernel / FW log",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Raw log evidence") { Patterns = ["*.log", "*.txt", "*.dmesg", "*.trace"] }, FilePickerFileTypes.All],
            });
            return files.SingleOrDefault()?.TryGetLocalPath();
        }, ComputeEvidenceSha256Async);
    }

    internal async Task AttachSelectedMarkerEvidenceAsync(
        Func<Task<string?>> selectPath,
        Func<string, Task<string>> computeSha256)
    {
        ArgumentNullException.ThrowIfNull(selectPath);
        ArgumentNullException.ThrowIfNull(computeSha256);
        if (!TryGetSelectedMarker(out var marker)) return;
        var markerId = marker.Id;
        var operationIdentity = CaptureReviewOperationIdentity();
        var path = await selectPath();
        if (path is null) return;
        if (!IsCurrentReviewOperation(operationIdentity))
        {
            SessionStatusText.Text = "Evidence attachment discarded · capture changed while selecting evidence";
            return;
        }
        try
        {
            var hash = await computeSha256(path);
            if (!IsCurrentReviewOperation(operationIdentity))
            {
                SessionStatusText.Text = "Evidence attachment discarded · capture changed while hashing evidence";
                return;
            }
            var name = Path.GetFileName(path);
            var lowerName = name.ToLowerInvariant();
            var kind = lowerName.Contains("kernel") || lowerName.Contains("dmesg")
                ? ReplayEvidenceKind.KernelLog
                : lowerName.Contains("fw") || lowerName.Contains("firmware")
                    ? ReplayEvidenceKind.FirmwareLog
                    : ReplayEvidenceKind.Other;
            reviewWorkspace!.AddMarkerEvidence(markerId, new ReplayEvidenceReference(
                $"evidence-{Guid.NewGuid():N}", kind, path, hash, name));
            RefreshWorkspaceAfterReviewMutation();
            SessionStatusText.Text = $"Raw evidence attached · {name} · SHA-256 recorded";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            SessionStatusText.Text = $"Evidence attach failed · {exception.Message}";
        }
    }

    private static async Task<string> ComputeEvidenceSha256Async(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private async void SaveReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (session is null || decodeConfiguration is null || reviewWorkspace is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save replay review",
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.SourcePath) + ".nvtreplay.json",
            DefaultExtension = "json",
            FileTypeChoices = [new FilePickerFileType("NVT replay review") { Patterns = ["*.nvtreplay.json"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) return;
        try
        {
            var document = new ReplaySidecarDocument(
                ReplaySidecarDocument.CurrentSchemaVersion,
                session.SourceSha256,
                Path.GetFileName(session.SourcePath),
                decodeConfiguration,
                reviewWorkspace.Markers.ToArray(),
                reviewWorkspace.ReviewSession.ExportState(),
                new Dictionary<string, bool>
                {
                    ["pauseOnAlarm"] = SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail,
                    ["pauseOnBreak"] = SettingsPage.PlaybackPreferences.PauseOnBreak,
                    ["pauseOnAllBreak"] = SettingsPage.PlaybackPreferences.PauseOnAllBreak,
                    ["compressIdle"] = CompressIdleCheckBox.IsChecked == true,
                },
                DateTimeOffset.UtcNow);
            await new ReplaySidecarStore().SaveAsync(path, document);
            SessionStatusText.Text = $"Review saved atomically · {Path.GetFileName(path)}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SessionStatusText.Text = $"Review save failed · {exception.Message}";
        }
    }

    private async void LoadReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await LoadReviewSidecarAsync(async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Open replay review",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("NVT replay review") { Patterns = ["*.nvtreplay.json"] }],
            });
            return files.SingleOrDefault()?.TryGetLocalPath();
        }, (path, sourceSha256, configuration) =>
            new ReplaySidecarStore().LoadAsync(path, sourceSha256, configuration));
    }

    internal async Task LoadReviewSidecarAsync(
        Func<Task<string?>> selectPath,
        Func<string, string, ReplayDecodeConfiguration, Task<ReplaySidecarOpenResult>> loadSidecar)
    {
        ArgumentNullException.ThrowIfNull(selectPath);
        ArgumentNullException.ThrowIfNull(loadSidecar);
        if (session is null || decodeConfiguration is null || reviewWorkspace is null) return;
        var operationIdentity = CaptureReviewOperationIdentity();
        var configuration = decodeConfiguration;
        var path = await selectPath();
        if (path is null) return;
        if (!IsCurrentReviewOperation(operationIdentity))
        {
            DiscardPendingSidecar("Review load discarded · capture changed while selecting a sidecar");
            return;
        }
        try
        {
            var loaded = await loadSidecar(path, operationIdentity.SourceSha256, configuration);
            if (!IsCurrentReviewOperation(operationIdentity))
            {
                DiscardPendingSidecar("Review load discarded · capture changed while opening the sidecar");
                return;
            }
            ValidateSidecarOpenResult(loaded);
            if (loaded.RequiresExplicitConfirmation)
            {
                pendingSidecar = new PendingReviewSidecar(loaded, operationIdentity);
                ApplySidecarButton.IsVisible = true;
                LoadReviewButton.IsVisible = false;
                SessionStatusText.Text = $"Review mismatch not applied · {string.Join(" ", loaded.Warnings)}";
                return;
            }
            TryApplySidecar(loaded, operationIdentity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or UnsupportedReplaySidecarVersionException or ArgumentException)
        {
            SessionStatusText.Text = $"Review open failed · {exception.Message}";
        }
    }

    private void ApplySidecarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (pendingSidecar is not { } pending) return;
        if (!IsCurrentReviewOperation(pending.OperationIdentity))
        {
            DiscardPendingSidecar("Review confirmation discarded · capture changed after the sidecar was opened");
            return;
        }
        TryApplySidecar(pending.Result, pending.OperationIdentity);
    }

    internal void ConfirmPendingSidecarForTesting() => ApplySidecarButton_OnClick(null, new RoutedEventArgs());

    private bool TryApplySidecar(ReplaySidecarOpenResult loaded, ReviewOperationIdentity operationIdentity)
    {
        if (!IsCurrentReviewOperation(operationIdentity))
        {
            DiscardPendingSidecar("Review apply discarded · capture changed before the sidecar could be applied");
            return false;
        }
        try
        {
            ApplySidecar(loaded);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            DiscardPendingSidecar($"Review not applied · invalid sidecar · {exception.Message}");
            return false;
        }
    }

    private void DiscardPendingSidecar(string status)
    {
        pendingSidecar = null;
        ApplySidecarButton.IsVisible = false;
        LoadReviewButton.IsVisible = true;
        SessionStatusText.Text = status;
    }

    private void ApplySidecar(ReplaySidecarOpenResult loaded)
    {
        ValidateSidecarOpenResult(loaded);
        var resolvedById = loaded.Evidence.ToDictionary(item => item.Reference.Id, item => item.ResolvedPath, StringComparer.Ordinal);
        var resolvedMarkers = loaded.Document.Markers.Select(marker => marker with
        {
            Evidence = marker.Evidence?.Select(reference => reference with
            {
                Path = resolvedById.GetValueOrDefault(reference.Id, reference.Path),
            }).ToArray(),
        }).ToArray();
        var currentPreferences = SettingsPage.PlaybackPreferences;
        var importedPreferences = new ReplayPlaybackPreferences(
            loaded.Document.VisibilityPreferences.TryGetValue("pauseOnAlarm", out var pauseOnAlarm)
                ? pauseOnAlarm
                : currentPreferences.PauseOnAlarmOrQaFail,
            loaded.Document.VisibilityPreferences.GetValueOrDefault("pauseOnBreak"),
            loaded.Document.VisibilityPreferences.GetValueOrDefault("pauseOnAllBreak"));
        var workspace = reviewWorkspace ?? throw new InvalidOperationException("A decoded review workspace is required.");
        var import = workspace.ReplaceMarkersAndImportState(resolvedMarkers, loaded.Document.ReviewStates);
        SettingsPage.SetPlaybackPreferences(importedPreferences);
        ApplyPlaybackPreferences(importedPreferences, syncTransportControls: true);
        if (loaded.Document.VisibilityPreferences.TryGetValue("compressIdle", out var compress)) CompressIdleCheckBox.IsChecked = compress;
        RefreshWorkspaceAfterReviewMutation();
        pendingSidecar = null;
        ApplySidecarButton.IsVisible = false;
        LoadReviewButton.IsVisible = true;
        SessionStatusText.Text =
            $"Review opened · {import.MarkerCount} markers · {loaded.Evidence.Count} evidence refs" +
            (loaded.Warnings.Count > 0 ? $" · {loaded.Warnings.Count} warnings" : string.Empty) +
            (import.UnresolvedReviewGroupIds.Count > 0 ? $" · {import.UnresolvedReviewGroupIds.Count} stale review groups" : string.Empty);
    }

    private static void ValidateSidecarOpenResult(ReplaySidecarOpenResult loaded)
    {
        ArgumentNullException.ThrowIfNull(loaded);
        ArgumentNullException.ThrowIfNull(loaded.Document);
        ArgumentNullException.ThrowIfNull(loaded.Document.Markers);
        ArgumentNullException.ThrowIfNull(loaded.Document.ReviewStates);
        ArgumentNullException.ThrowIfNull(loaded.Document.VisibilityPreferences);
        ArgumentNullException.ThrowIfNull(loaded.Evidence);
        ArgumentNullException.ThrowIfNull(loaded.Warnings);
        if (loaded.Evidence.Any(item => item is null))
            throw new ArgumentException("Sidecar evidence resolution cannot contain null entries.", nameof(loaded));
    }

    private sealed record PendingReviewSidecar(
        ReplaySidecarOpenResult Result,
        ReviewOperationIdentity OperationIdentity);

}
