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
    private ReviewInspectorWorkspace? reviewWorkspace;
    private ReviewGroupRow[] reviewRows = [];
    private bool reviewRailCollapsed;
    private bool inspectorRailCollapsed;
    private bool? reviewRailUserPreference;
    private bool? inspectorRailUserPreference;
    private SourceRecord? currentInspectorRecord;
    private object? currentInspectorFrame;
    private ITouchReplaySnapshot? currentInspectorSnapshot;
    private InspectorFramePresentation? currentInspectorPresentation;
    private bool synchronizingReviewSelection;
    private double expandedInspectorRailWidth = 286;
    private const double ExpandedReviewRailWidth = 260;
    private const double DefaultInspectorRailWidth = 286;
    private const double MinimumInspectorRailWidth = 280;
    private const double MaximumInspectorRailWidth = 420;
    private const double CollapsedInspectorRailWidth = 58;
    private static readonly TimeSpan PlaybackDetailPresentationInterval = TimeSpan.FromMilliseconds(100);
    private long lastDetailPresentationTimestamp;
    internal int DetailPresentationCount { get; private set; }
    internal ReviewInspectorWorkspace? ReviewWorkspace => reviewWorkspace;
    private void ReviewRailToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        reviewRailUserPreference = !reviewRailCollapsed;
        SetReviewRailCollapsed(reviewRailUserPreference.Value);
    }

    private void InspectorRailToggleButton_OnClick(object? sender, RoutedEventArgs e)
    {
        inspectorRailUserPreference = !inspectorRailCollapsed;
        SetInspectorRailCollapsed(inspectorRailUserPreference.Value);
    }

    private async void CopyInspectorButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SynchronizeVisibleDecodedInspector();
        if (currentInspectorPresentation is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        await clipboard.SetTextAsync(InspectorPresentationBuilder.BuildCopyText(currentInspectorPresentation));
        SessionStatusText.Text = $"Copied frame {currentInspectorPresentation.LogicalLabel} summary";
    }

    private void PreviousFindingButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFinding(-1);

    private void NextFindingButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFinding(1);

    private void NavigateFinding(int direction)
    {
        if (reviewWorkspace is null || replaySession is null) return;
        var occurrence = reviewWorkspace.NavigateFinding(direction < 0
            ? ReviewFindingDirection.Previous
            : ReviewFindingDirection.Next);
        if (occurrence?.LogicalIndex is not { } target) return;
        StopPlayback();
        SeekReplay(target);
        SynchronizeReviewSelectionFromWorkspace(scrollIntoView: true);
    }

    private void InspectorContactsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selectedId = (InspectorContactsList.SelectedItem as InspectorContactRow)?.Id;
        if (reviewWorkspace is not null)
        {
            if (selectedId is { } id) reviewWorkspace.SelectContact(id);
            else reviewWorkspace.ClearContactSelection();
        }
        PaintSurface.SetHighlightedContact(selectedId);
    }

    private void SetReviewRailCollapsed(bool collapsed)
    {
        reviewRailCollapsed = collapsed;
        WorkspaceShellGrid.ColumnDefinitions[0].Width = new GridLength(collapsed ? 0 : ExpandedReviewRailWidth);
        ReviewRailBorder.IsVisible = !collapsed;
        ReviewRailTitle.IsVisible = !collapsed;
        ReviewRailCountBadge.IsVisible = !collapsed;
        ClearMarkersButton.IsVisible = !collapsed;
        ReviewRailContent.IsVisible = !collapsed;
        ReviewRailToggleButton.Content = collapsed ? "\uE76C" : "\uE76B";
        ToolTip.SetTip(ReviewRailToggleButton, collapsed ? "Open review queue" : "Collapse review queue");
    }

    private void SetInspectorRailCollapsed(bool collapsed)
    {
        var wasCollapsed = inspectorRailCollapsed;
        inspectorRailCollapsed = collapsed;
        var column = WorkspaceShellGrid.ColumnDefinitions[3];
        if (collapsed)
        {
            if (column.ActualWidth >= MinimumInspectorRailWidth)
                expandedInspectorRailWidth = Math.Clamp(column.ActualWidth, MinimumInspectorRailWidth, MaximumInspectorRailWidth);
            column.MinWidth = 0;
            column.MaxWidth = CollapsedInspectorRailWidth;
            column.Width = new GridLength(CollapsedInspectorRailWidth);
        }
        else
        {
            if (!wasCollapsed && column.ActualWidth >= MinimumInspectorRailWidth)
                expandedInspectorRailWidth = Math.Clamp(column.ActualWidth, MinimumInspectorRailWidth, MaximumInspectorRailWidth);
            column.MinWidth = MinimumInspectorRailWidth;
            column.MaxWidth = MaximumInspectorRailWidth;
            column.Width = new GridLength(Math.Clamp(
                expandedInspectorRailWidth <= 0 ? DefaultInspectorRailWidth : expandedInspectorRailWidth,
                MinimumInspectorRailWidth,
                MaximumInspectorRailWidth));
        }
        InspectorGridSplitter.IsVisible = !collapsed;
        InspectorRailTitle.IsVisible = !collapsed;
        CopyInspectorButton.IsVisible = !collapsed;
        InspectorRailContent.IsVisible = !collapsed;
        InspectorCollapsedSummary.IsVisible = collapsed;
        InspectorRailToggleButton.Content = collapsed ? "\uE76B" : "\uE76C";
        ToolTip.SetTip(InspectorRailToggleButton, collapsed ? "Open inspector" : "Collapse inspector");
    }

    private ReviewOperationIdentity CaptureReviewOperationIdentity()
    {
        var loadedSession = session ?? throw new InvalidOperationException("A loaded capture is required.");
        return new ReviewOperationIdentity(
            loadedSession,
            reviewWorkspace,
            decodeConfiguration,
            loadedSession.SourceSha256,
            activeCaptureLoadGeneration,
            activeCaptureDecodeGeneration);
    }

    private bool IsCurrentReviewOperation(ReviewOperationIdentity identity) =>
        ReferenceEquals(session, identity.Session) &&
        ReferenceEquals(reviewWorkspace, identity.Workspace) &&
        decodeConfiguration == identity.DecodeConfiguration &&
        string.Equals(session?.SourceSha256, identity.SourceSha256, StringComparison.OrdinalIgnoreCase) &&
        activeCaptureLoadGeneration == identity.LoadGeneration &&
        activeCaptureDecodeGeneration == identity.DecodeGeneration;

    private void RawRecordsList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (RawRecordsList.SelectedItem is RawRecordRow row)
        {
            RegisterActivitySurface.SetSelected(row.Record.StableId);
            ShowRecord(row.Record, FindFrame(row.Record.StableId));
            if (!synchronizingSelection && replaySession is not null)
            {
                var logicalIndex = Array.FindIndex(decodedRows, item =>
                    item.PhysicalRecords.Any(source => source.StableId == row.Record.StableId));
                if (logicalIndex >= 0)
                {
                    SeekReplay(logicalIndex);
                }
            }
        }
    }

    private void DecodedFramesList_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DecodedFramesList.SelectedItem is not DecodedFrameRow row)
        {
            return;
        }

        if (!synchronizingSelection && replaySession is not null)
        {
            SeekReplay(row.LogicalIndex);
            return;
        }

        var physical = row.PhysicalRecords[^1];
        ShowRecord(physical, row.Frame);
        if (rawRowsById.TryGetValue(physical.StableId, out var rawRow))
        {
            RawRecordsList.SelectedItem = rawRow;
            RawRecordsList.ScrollIntoView(rawRow);
        }
    }

    private void DiagnosticListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizingReviewSelection) return;
        if (DiagnosticListBox.SelectedItem is not ReviewGroupRow row)
        {
            reviewWorkspace?.ClearFindingSelection();
            ClearReviewSelectionPresentation();
            return;
        }
        if (reviewWorkspace is null) return;
        var occurrence = reviewWorkspace.SelectFinding(row.Group.Id);
        PresentReviewSelection(row.Group, occurrence.Id);
        NavigateToOccurrence(occurrence);
    }

    private void ReviewGroupBorder_OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control) return;
        if (control.DataContext is not ReviewGroupRow { CanUnmark: true } row)
        {
            CloseContextMenu(control);
            return;
        }
        DiagnosticListBox.SelectedItem = row;
        var matchingMarkers = reviewWorkspace?.Markers.Where(marker => marker.Id == row.MarkerId).ToArray() ?? [];
        if (matchingMarkers.Length == 0)
        {
            CloseContextMenu(control);
            return;
        }
        OpenMarkerContextMenu(control, matchingMarkers);
        e.Handled = true;
    }

    private void ReplayTimelineSurface_OnMarkerContextRequested(object? sender, ReplayTimelineContextEventArgs e)
    {
        var matchingMarkers = (reviewWorkspace?.Markers ?? [])
            .Where(marker => marker.StartLogicalIndex <= e.LogicalIndex && marker.EndLogicalIndex >= e.LogicalIndex)
            .OrderBy(marker => marker.StartLogicalIndex)
            .ThenBy(marker => marker.CreatedAt)
            .ToArray();
        if (matchingMarkers.Length == 0)
        {
            CloseContextMenu(ReplayTimelineSurface);
            return;
        }
        OpenMarkerContextMenu(ReplayTimelineSurface, matchingMarkers);
        e.Handled = true;
    }

    private void OpenMarkerContextMenu(Control target, IEnumerable<ReplayMarker> matchingMarkers)
    {
        var items = matchingMarkers.Select(marker =>
        {
            var range = marker.IsRange
                ? $"frames {marker.StartLogicalIndex + 1:N0}-{marker.EndLogicalIndex + 1:N0}"
                : $"frame {marker.StartLogicalIndex + 1:N0}";
            var item = new MenuItem { Header = $"Unmark {marker.Label} · {range}", Tag = marker.Id };
            item.Click += (_, _) =>
            {
                CloseContextMenu(target);
                RemoveMarker(marker.Id);
            };
            return item;
        }).ToArray();
        if (items.Length == 0) return;
        CloseContextMenu(target);
        var menu = new ContextMenu { ItemsSource = items };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(target.ContextMenu, menu)) target.ContextMenu = null;
        };
        target.ContextMenu = menu;
        menu.Open(target);
    }

    private static void CloseContextMenu(Control target)
    {
        var menu = target.ContextMenu;
        if (menu is not null) menu.Close();
        if (ReferenceEquals(target.ContextMenu, menu)) target.ContextMenu = null;
    }

    private void ReviewOccurrenceComboBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (synchronizingReviewSelection || reviewWorkspace is null ||
            ReviewOccurrenceComboBox.SelectedItem is not ReviewOccurrenceRow row)
            return;
        var occurrence = reviewWorkspace.SelectOccurrence(row.Number - 1);
        NavigateToOccurrence(occurrence);
    }

    private void NavigateToOccurrence(ReviewOccurrence occurrence)
    {
        if (occurrence.LogicalIndex is { } logicalIndex && replaySession is { Count: > 0 })
            SeekReplay(logicalIndex);
        else if (rawRowsById.TryGetValue(occurrence.Diagnostic.SourceRecordId, out var rawRow))
        {
            ShowRecord(rawRow.Record, FindFrame(rawRow.Record.StableId));
            RawRecordsList.SelectedItem = rawRow;
            RawRecordsList.ScrollIntoView(rawRow);
        }
        InspectorSubtitleText.Text = $"{occurrence.Diagnostic.Severity} · {occurrence.Diagnostic.Code} · {occurrence.Diagnostic.Message}";
    }

    private void ReviewFilterButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } tag } && Enum.TryParse<ReviewQueueFilter>(tag.ToString(), out var filter))
        {
            reviewWorkspace?.SetFilter(filter);
            RefreshReviewQueue();
        }
    }

    private void AcknowledgeReviewButton_OnClick(object? sender, RoutedEventArgs e) =>
        MutateReview(groupId => reviewWorkspace!.AcknowledgeFinding(groupId));

    private void DispositionReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: { } tag } && Enum.TryParse<ReviewDisposition>(tag.ToString(), out var disposition))
            MutateReview(groupId => reviewWorkspace!.SetFindingDisposition(groupId, disposition));
    }

    private void ResolveReviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            MutateReview(groupId => reviewWorkspace!.ResolveFinding(groupId));
        }
        catch (InvalidOperationException exception)
        {
            ReviewStateText.Text = $"Cannot resolve · {exception.Message}";
        }
    }

    private void MutateReview(Func<string, ReviewEventGroup> action)
    {
        if (reviewWorkspace?.SelectedFindingGroupId is not { } groupId) return;
        var updated = action(groupId);
        RefreshReviewQueue();
        UpdateReviewState(updated);
    }

    private void UpdateReviewState(ReviewEventGroup group)
    {
        var details = group.Occurrences.FirstOrDefault()?.Diagnostic.Details;
        var qa = details?.GetValueOrDefault("qa_case_ids");
        var evidence = details?.GetValueOrDefault("evidence");
        ReviewStateText.Text =
            $"captured={group.CurrentCapturedAlarmState} · workflow={group.WorkflowState} · " +
            $"lifecycle={group.AsilLifecycle?.ToString() ?? "-"} · disposition={group.Disposition}" +
            (string.IsNullOrWhiteSpace(qa) ? string.Empty : $"\nQA={qa}") +
            (string.IsNullOrWhiteSpace(evidence) || evidence == "-" ? string.Empty : $"\nevidence={evidence}");
    }

    private void PresentReviewSelection(ReviewEventGroup group, string? occurrenceId)
    {
        var marker = reviewWorkspace?.SelectedMarker;
        var isMarker = marker is not null;
        AnnotationMetadataPanel.IsVisible = marker is not null;
        MarkerNameTextBox.Text = marker?.Label ?? string.Empty;
        MarkerQaCaseTextBox.Text = marker is null
            ? string.Empty
            : string.Join(", ", marker.QaCaseIds ?? []);
        ReviewActionsPanel.IsVisible = true;
        ReviewEmptyText.IsVisible = false;
        var occurrences = group.Occurrences
            .Select((occurrence, index) => new ReviewOccurrenceRow(index + 1, occurrence))
            .ToArray();
        synchronizingReviewSelection = true;
        try
        {
            ReviewOccurrenceComboBox.ItemsSource = occurrences;
            ReviewOccurrenceComboBox.SelectedItem = occurrences.FirstOrDefault(row =>
                row.Occurrence.Id.Equals(occurrenceId, StringComparison.Ordinal)) ?? occurrences.FirstOrDefault();
        }
        finally
        {
            synchronizingReviewSelection = false;
        }
        ReviewSelectionTitleText.Text = isMarker ? "MARKER FRAME" : "SELECTED FINDING";
        ReviewOccurrenceComboBox.IsVisible = !isMarker;
        MarkerOccurrenceText.IsVisible = isMarker;
        MarkerOccurrenceText.Text = isMarker ? occurrences.FirstOrDefault()?.FrameAndSourceLabel ?? string.Empty : string.Empty;
        FindingWorkflowActionsPanel.IsVisible = !isMarker;
        ReviewStateText.IsVisible = !isMarker;
        if (!isMarker)
        {
            ComboBoxAutoSizer.Fit(ReviewOccurrenceComboBox);
            UpdateReviewState(group);
        }
        else
        {
            ReviewStateText.Text = string.Empty;
        }
    }

    private void ClearReviewSelectionPresentation()
    {
        ReviewActionsPanel.IsVisible = false;
        ReviewEmptyText.IsVisible = true;
        AnnotationMetadataPanel.IsVisible = false;
        ReviewSelectionTitleText.Text = "SELECTED FINDING";
        ReviewOccurrenceComboBox.IsVisible = true;
        MarkerOccurrenceText.IsVisible = false;
        MarkerOccurrenceText.Text = string.Empty;
        FindingWorkflowActionsPanel.IsVisible = true;
        ReviewStateText.IsVisible = true;
        ReviewStateText.Text = string.Empty;
        MarkerNameTextBox.Text = string.Empty;
        MarkerQaCaseTextBox.Text = string.Empty;
        ReviewOccurrenceComboBox.ItemsSource = null;
    }

    private void SynchronizeReviewSelectionFromWorkspace(bool scrollIntoView)
    {
        if (reviewWorkspace?.SelectedFindingGroupId is not { } groupId)
        {
            ClearReviewSelectionPresentation();
            return;
        }
        var row = reviewRows.FirstOrDefault(item => item.Group.Id.Equals(groupId, StringComparison.Ordinal));
        if (row is null)
        {
            ClearReviewSelectionPresentation();
            return;
        }
        synchronizingReviewSelection = true;
        try
        {
            DiagnosticListBox.SelectedItem = row;
            if (scrollIntoView) DiagnosticListBox.ScrollIntoView(row);
        }
        finally
        {
            synchronizingReviewSelection = false;
        }
        PresentReviewSelection(row.Group, reviewWorkspace.SelectedOccurrenceId);
    }

    private void CreateReviewWorkspace(
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyList<ITouchReplaySnapshot>? frames = null,
        IReadOnlyList<ReplayMarker>? replacementMarkers = null,
        IReadOnlyList<ReviewStateSnapshot>? reviewState = null,
        ReviewWorkspaceState? workspaceState = null)
    {
        workspaceState ??= CaptureReviewWorkspaceState();
        var markers = replacementMarkers ?? reviewWorkspace?.Markers.ToArray() ?? [];
        var state = reviewState ?? reviewWorkspace?.ReviewSession.ExportState() ?? [];
        diagnosticLineNumbers = diagnosticRows
            .Select(row => row.Diagnostic.Location.LineNumber)
            .OrderBy(line => line)
            .ToArray();
        reviewWorkspace = new ReviewInspectorWorkspace(
            frames ?? reviewWorkspace?.Frames ?? [],
            diagnostics,
            session?.RegisterAnnotations,
            markers,
            CurrentPaintMode,
            reviewOptions: workspaceState?.ReviewOptions ?? new ReviewSessionOptions(
                    SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail,
                    SettingsPage.PlaybackPreferences.PauseOnAlarmOrQaFail));
        reviewWorkspace.ReplaceMarkersAndImportState(markers, state);
        RestoreReviewWorkspaceState(reviewWorkspace, workspaceState);
        RefreshReviewQueue();
    }

    private ReviewWorkspaceState? CaptureReviewWorkspaceState() => reviewWorkspace is null
        ? null
        : new ReviewWorkspaceState(
            reviewWorkspace.Filter,
            reviewWorkspace.CurrentLogicalIndex,
            reviewWorkspace.SelectedFindingGroupId,
            reviewWorkspace.SelectedOccurrenceId,
            reviewWorkspace.SelectedMarkerId,
            reviewWorkspace.SelectedContactId,
            reviewWorkspace.ReviewOptions);

    private static void RestoreReviewWorkspaceState(
        ReviewInspectorWorkspace workspace,
        ReviewWorkspaceState? state)
    {
        if (state is null) return;
        workspace.SetReviewOptions(state.ReviewOptions);
        workspace.SetFilter(state.Filter);
        var logicalIndex = workspace.Frames.Count == 0
            ? -1
            : Math.Clamp(state.CurrentLogicalIndex, 0, workspace.Frames.Count - 1);
        if (logicalIndex >= 0) workspace.SelectFrame(logicalIndex);

        var finding = state.SelectedFindingGroupId is { } groupId
            ? workspace.VisibleGroups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.Ordinal))
            : null;
        if (finding is not null)
        {
            var occurrenceIndex = state.SelectedOccurrenceId is { } occurrenceId
                ? finding.Occurrences.ToList().FindIndex(occurrence =>
                    occurrence.Id.Equals(occurrenceId, StringComparison.Ordinal))
                : 0;
            workspace.SelectFinding(finding.Id, Math.Max(0, occurrenceIndex));
        }
        else if (state.SelectedMarkerId is { } markerId)
        {
            workspace.SelectMarker(markerId);
        }

        if (logicalIndex >= 0) workspace.SelectFrame(logicalIndex);
        if (state.SelectedContactId is { } contactId) workspace.SelectContact(contactId);
    }

    private void SynchronizeReviewWorkspaceConsumers()
    {
        RefreshPaintAnnotations();
        RefreshOutputPreviewIfVisible();
        if (reviewWorkspace?.CurrentSnapshot is { } snapshot)
            ShowRecord(snapshot.PrimarySource, snapshot.DecodedFrame, snapshot);
        else if (currentInspectorRecord is not null)
            ShowRecord(currentInspectorRecord, currentInspectorFrame, currentInspectorSnapshot);
    }

    private void RefreshReviewQueue()
    {
        reviewRows = reviewWorkspace?.VisibleGroups.Select(group => new ReviewGroupRow(group)).ToArray() ?? [];
        synchronizingReviewSelection = true;
        try
        {
            DiagnosticListBox.ItemsSource = reviewRows;
            var selected = reviewWorkspace?.SelectedFindingGroupId is { } groupId
                ? reviewRows.FirstOrDefault(row => row.Group.Id == groupId)
                : null;
            DiagnosticListBox.SelectedItem = selected;
        }
        finally
        {
            synchronizingReviewSelection = false;
        }
        DiagnosticCountText.Text = reviewRows.Length.ToString(CultureInfo.InvariantCulture);
        ClearMarkersButton.IsEnabled = reviewWorkspace?.Markers.Count > 0;
        ReplayTimelineSurface.SetMarkerFrames(reviewWorkspace?.Markers.Select(marker => marker.StartLogicalIndex) ?? []);
        if (DiagnosticListBox.SelectedItem is ReviewGroupRow selectedRow)
            PresentReviewSelection(selectedRow.Group, reviewWorkspace?.SelectedOccurrenceId);
        else
            ClearReviewSelectionPresentation();
        if (Bounds.Width > 0) ApplyResponsiveRails(Bounds.Width);
    }

    private void AddMarkerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (currentLogicalIndex < 0 || reviewWorkspace is null) return;
        reviewWorkspace.SelectFrame(currentLogicalIndex);
        var marker = reviewWorkspace.AddMarker();
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = $"Added marker · frame {currentLogicalIndex + 1}";
    }

    private void UnmarkButton_OnClick(object? sender, RoutedEventArgs e) => RemoveMarker(reviewWorkspace?.SelectedMarkerId);

    private void RemoveMarker(string? markerId)
    {
        if (markerId is null || reviewWorkspace?.Markers.FirstOrDefault(marker => marker.Id == markerId) is not { } removed)
            return;
        if (!reviewWorkspace.RemoveMarker(markerId)) return;
        CloseContextMenu(ReplayTimelineSurface);
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = removed.IsRange
            ? $"Removed range marker · frames {removed.StartLogicalIndex + 1}-{removed.EndLogicalIndex + 1}"
            : $"Removed marker · frame {removed.StartLogicalIndex + 1}";
    }

    private void RenameMarkerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMarker(out var marker)) return;
        var label = (MarkerNameTextBox.Text ?? string.Empty).Trim();
        if (label.Length == 0)
        {
            SessionStatusText.Text = "Marker name cannot be empty";
            MarkerNameTextBox.Text = marker.Label;
            return;
        }
        try
        {
            reviewWorkspace!.RenameMarker(marker.Id, label);
            RefreshWorkspaceAfterReviewMutation();
            SessionStatusText.Text = $"Renamed marker · {label}";
        }
        catch (ArgumentException exception)
        {
            SessionStatusText.Text = exception.Message;
            MarkerNameTextBox.Text = marker.Label;
        }
    }

    private void ClearMarkersButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (reviewWorkspace is null) return;
        var count = reviewWorkspace.ClearMarkers();
        if (count == 0) return;
        CloseContextMenu(ReplayTimelineSurface);
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = $"Cleared {count} marker{(count == 1 ? string.Empty : "s")}";
    }

    private void ApplyMarkerQaButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMarker(out var marker)) return;
        var qaCaseIds = (MarkerQaCaseTextBox.Text ?? string.Empty)
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        reviewWorkspace!.SetMarkerQaCaseIds(marker.Id, qaCaseIds);
        RefreshWorkspaceAfterReviewMutation();
        SessionStatusText.Text = $"QA references updated · {qaCaseIds.Length} IDs";
    }

    private bool TryGetSelectedMarker(out ReplayMarker marker)
    {
        marker = reviewWorkspace?.SelectedMarker!;
        return marker is not null;
    }

    private void RefreshWorkspaceAfterReviewMutation()
    {
        RefreshReviewQueue();
        RefreshPaintAnnotations();
        RefreshOutputPreviewIfVisible();
    }

    private object? FindFrame(string sourceId) =>
        decodedRowsBySourceId.GetValueOrDefault(sourceId)?.Frame;

    private void ShowRecord(SourceRecord record, object? frame, ITouchReplaySnapshot? snapshot = null)
    {
        var registerAnnotation = session?.RegisterAnnotations.Find(record.StableId);
        currentInspectorRecord = record;
        currentInspectorFrame = frame;
        currentInspectorSnapshot = snapshot;
        currentInspectorPresentation = snapshot is not null &&
            reviewWorkspace?.CurrentSnapshot?.LogicalIndex == snapshot.LogicalIndex
                ? reviewWorkspace.CurrentPresentation
                : InspectorPresentationBuilder.Build(
                    record,
                    frame,
                    snapshot,
                    replaySession?.Count ?? 0,
                    CurrentPaintMode,
                    registerAnnotation);
        currentInspectorPresentation ??= InspectorPresentationBuilder.Build(
            record,
            frame,
            snapshot,
            replaySession?.Count ?? 0,
            CurrentPaintMode,
            registerAnnotation);
        if (snapshot is not null)
            currentInspectorPresentation = currentInspectorPresentation with
            {
                TimestampLabel = FormatClock(SelectedTime(snapshot.Timeline)),
            };
        ApplyInspectorPresentation(currentInspectorPresentation, frame is null && registerAnnotation is not null);

        var frameAlerts = reviewWorkspace?.ReviewSession.FrameAlerts(record.StableId, record.Location.LineNumber) ?? [];
        var crcFailed = currentInspectorPresentation.CrcFailed;
        var asilAlarm = currentInspectorPresentation.AsilAlarm;
        InspectorAlertBorder.IsVisible = frameAlerts.Count > 0 || crcFailed || asilAlarm;
        InspectorAlertText.Text = frameAlerts.Count > 0
            ? string.Join(" · ", frameAlerts.Select(item => EngineeringDisplayText.AddBreakOpportunities(item.Code)))
            : crcFailed ? "CRC validation failed" : asilAlarm ? "ASIL alarm active" : string.Empty;
        var hasNavigableFindings = reviewWorkspace?.ReviewSession.HasNavigableFindings == true;
        PreviousFindingButton.IsVisible = hasNavigableFindings;
        NextFindingButton.IsVisible = hasNavigableFindings;
        SourceLineText.Text = record.Location.LineNumber.ToString(CultureInfo.InvariantCulture);
        SourceOffsetText.Text = record.Location.ByteOffset.ToString(CultureInfo.InvariantCulture);
        StableIdText.Text = record.StableId;
        TransportFieldsItemsControl.ItemsSource = BuildTransportRows(record);
        SourceFieldsText.Text = FormatSourceFields(record);
        var rawLayout = RawFrameLayoutBuilder.Build(record, frame);
        RawLayoutTitleText.Text = rawLayout.Title;
        RawLayoutSummaryText.Text = rawLayout.Summary;
        RawByteSectionsItemsControl.ItemsSource = rawLayout.Sections;
        RawBytesText.Text = FormatBytes(record.Data);
        ProtocolFieldsItemsControl.ItemsSource = currentInspectorPresentation.ProtocolRows;
    }

    private void SynchronizeVisibleDecodedInspector()
    {
        if (currentInspectorSnapshot is null || reviewWorkspace?.CurrentSnapshot is not { } snapshot ||
            currentInspectorSnapshot.LogicalIndex == snapshot.LogicalIndex)
            return;
        ShowRecord(snapshot.PrimarySource, snapshot.DecodedFrame, snapshot);
    }

    private void ApplyInspectorPresentation(InspectorFramePresentation presentation, bool registerActivity)
    {
        var selectedId = currentInspectorSnapshot is not null
            ? reviewWorkspace?.SelectedContactId
            : (InspectorContactsList.SelectedItem as InspectorContactRow)?.Id;
        InspectorTitleText.Text = registerActivity ? "Register" : "Frame";
        InspectorLogicalText.Text = presentation.LogicalLabel;
        InspectorTimestampText.Text = presentation.TimestampLabel;
        InspectorSubtitleText.Text = presentation.Subtitle;
        InspectorFingerText.Text = presentation.ContactSummary.Fingers.ToString(CultureInfo.InvariantCulture);
        InspectorGloveText.Text = presentation.ContactSummary.Gloves.ToString(CultureInfo.InvariantCulture);
        InspectorPalmText.Text = presentation.ContactSummary.Palms.ToString(CultureInfo.InvariantCulture);
        InspectorContactCountText.Text = presentation.ContactSummary.Total == 1
            ? "1 active"
            : $"{presentation.ContactSummary.Total} active";
        InspectorNoContactsText.IsVisible = false;
        InspectorContactsList.IsVisible = presentation.Contacts.Count > 0;
        InspectorContactsList.ItemsSource = presentation.Contacts;
        InspectorContactsList.SelectedItem = selectedId is { } id
            ? presentation.Contacts.FirstOrDefault(contact => contact.Id == id)
            : null;
        PaintSurface.SetHighlightedContact((InspectorContactsList.SelectedItem as InspectorContactRow)?.Id);

        InspectorCrcText.Text = presentation.CrcLabel;
        InspectorAsilText.Text = presentation.AsilLabel;
        InspectorAsilRawText.Text = presentation.AsilRawLabel is "-" ? string.Empty : presentation.AsilRawLabel;
        SetHealthText(InspectorCrcText, presentation.CrcFailed, presentation.CrcLabel != "-");
        SetHealthText(InspectorAsilText, presentation.AsilAlarm, presentation.AsilLabel != "-");
        InspectorAllBreakBadge.IsVisible = presentation.AllBreak;
        InspectorAllBreakText.Text = "ALL BREAK";

        CollapsedFingerText.Text = presentation.ContactSummary.Fingers.ToString(CultureInfo.InvariantCulture);
        CollapsedGloveText.Text = presentation.ContactSummary.Gloves.ToString(CultureInfo.InvariantCulture);
        CollapsedPalmText.Text = presentation.ContactSummary.Palms.ToString(CultureInfo.InvariantCulture);
        CollapsedAsilText.Text = presentation.AsilLabel switch
        {
            "NORMAL" => "ASIL\nNORM",
            "ALARM" => "ASIL\nALRM",
            _ => "ASIL -",
        };
        ToolTip.SetTip(CollapsedAsilBadge, presentation.AsilLabel == "-"
            ? "ASIL unavailable"
            : $"ASIL {presentation.AsilLabel} {presentation.AsilRawLabel}");
        SetHealthState(CollapsedAsilBadge, presentation.AsilAlarm, presentation.AsilLabel != "-");
    }

    private static void SetHealthState(Border border, bool alarm, bool known)
    {
        border.Classes.Set("healthGood", known && !alarm);
        border.Classes.Set("healthAlarm", known && alarm);
    }

    private static void SetHealthText(TextBlock textBlock, bool alarm, bool known)
    {
        textBlock.Classes.Set("healthTextAlarm", known && alarm);
        textBlock.Classes.Set("healthTextNormal", !alarm || !known);
    }

    private static IReadOnlyList<InspectorDetailRow> BuildTransportRows(SourceRecord record)
    {
        var rows = new List<InspectorDetailRow>
        {
            new("Operation", $"{record.Operation} {record.Target}"),
            new("Register", record.Address is { } address ? $"0x{address:X}" : "Not reported"),
            new("Payload", record.DeclaredByteCount is { } declared
                ? $"{record.Data.Count} bytes, declared {declared}"
                : $"{record.Data.Count} bytes"),
        };
        if (record.I2c is not { } i2c) return rows;
        rows.Add(new InspectorDetailRow("Slave", $"0x{i2c.SlaveAddress:X2}"));
        rows.Add(new InspectorDetailRow("Address ACK", i2c.AddressAcknowledged switch
        {
            true => "ACK",
            false => "NAK",
            null => "Not reported",
        }));
        rows.Add(new InspectorDetailRow("Write", i2c.WriteCommands.Count == 0
            ? "None"
            : string.Join(" | ", i2c.WriteCommands.Select(FormatBytes))));
        rows.Add(new InspectorDetailRow("Data ACK", I2cAckSummary.Format(i2c.Acked)));
        if (!string.IsNullOrWhiteSpace(i2c.Error)) rows.Add(new InspectorDetailRow("Transport error", i2c.Error));
        return rows;
    }

    private string FormatSourceFields(SourceRecord record)
    {
        var fields = session?.RegisterAnnotations.EffectiveFields(record) ?? record.SourceFields;
        return fields is not { Count: > 0 }
            ? "No adapter-specific source fields."
            : string.Join('\n', fields
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(field => EngineeringDisplayText.FormatKeyValue(field.Key, field.Value)));
    }

    private sealed record ReviewWorkspaceState(
        ReviewQueueFilter Filter,
        int CurrentLogicalIndex,
        string? SelectedFindingGroupId,
        string? SelectedOccurrenceId,
        string? SelectedMarkerId,
        byte? SelectedContactId,
        ReviewSessionOptions ReviewOptions);

    private sealed record ReviewOperationIdentity(
        CaptureSession Session,
        ReviewInspectorWorkspace? Workspace,
        ReplayDecodeConfiguration? DecodeConfiguration,
        string SourceSha256,
        long LoadGeneration,
        long DecodeGeneration);

    private bool ShouldPresentPlaybackDetails()
    {
        if (!InspectorRailBorder.IsVisible || inspectorRailCollapsed) return false;
        var now = Stopwatch.GetTimestamp();
        if (lastDetailPresentationTimestamp != 0 &&
            Stopwatch.GetElapsedTime(lastDetailPresentationTimestamp, now) < PlaybackDetailPresentationInterval)
            return false;
        lastDetailPresentationTimestamp = now;
        return true;
    }

    private void PresentReplayDetails(ITouchReplaySnapshot snapshot)
    {
        lastDetailPresentationTimestamp = Stopwatch.GetTimestamp();
        DetailPresentationCount++;
        ShowRecord(snapshot.PhysicalRecords[^1], snapshot.DecodedFrame, snapshot);
    }

    private void SelectReviewAt(int logicalIndex)
    {
        if (reviewWorkspace is null) return;
        var row = reviewRows.FirstOrDefault(item => item.Group.Occurrences.Any(occurrence =>
            occurrence.LogicalIndex == logicalIndex &&
            (occurrence.Diagnostic.Severity == DiagnosticSeverity.Alarm || occurrence.Category == ReviewEventCategory.QaResult)));
        if (row is null) return;
        DiagnosticListBox.SelectedItem = row;
        DiagnosticListBox.ScrollIntoView(row);
    }

}
