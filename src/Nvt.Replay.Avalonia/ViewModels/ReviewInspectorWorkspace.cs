using System.Collections;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.ViewModels;

public enum ReviewFindingDirection
{
    Previous = -1,
    Next = 1,
}

public sealed record ReviewWorkspaceImportResult(
    int MarkerCount,
    IReadOnlyList<string> UnresolvedReviewGroupIds,
    bool MarkersChanged,
    bool ReviewStateChanged)
{
    public bool Changed => MarkersChanged || ReviewStateChanged;
}

/// <summary>
/// Headless state for the Review rail and Inspector. It retains the canonical snapshots,
/// diagnostics, and source records by reference; UI controls only project this state.
/// CurrentPresentation represents decoded replay frames only. A UI that also inspects arbitrary
/// Raw Explorer or register-only source records must keep that visible presentation as a separate
/// projector branch and route copy actions to whichever presentation is currently visible.
/// </summary>
public sealed class ReviewInspectorWorkspace
{
    private readonly IReadOnlyList<ITouchReplaySnapshot> frames;
    private readonly IReadOnlyList<ReplayDiagnostic> baseDiagnostics;
    private readonly RegisterAnnotationProjection? registerAnnotations;
    private readonly IReadOnlyDictionary<string, int> logicalIndexBySourceId;
    private readonly List<ReplayMarker> markers = [];
    private readonly IReadOnlyList<ReplayMarker> markerView;
    private readonly TimeProvider timeProvider;
    private readonly Func<string, bool> evidenceExists;
    private ReviewEventGroup[] visibleGroups = [];
    private IReadOnlyDictionary<string, ReviewEventGroup> visibleGroupsById =
        new Dictionary<string, ReviewEventGroup>(StringComparer.Ordinal);
    private NavigableOccurrence[] navigableOccurrences = [];
    private IReadOnlyDictionary<string, int> navigablePositionByOccurrenceId =
        new Dictionary<string, int>(StringComparer.Ordinal);
    private ReviewSession reviewSession = null!;
    private ReviewSessionOptions reviewOptions;
    private ReplayRenderMode renderMode;

    public ReviewInspectorWorkspace(
        IReadOnlyList<ITouchReplaySnapshot> frames,
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        RegisterAnnotationProjection? registerAnnotations = null,
        IEnumerable<ReplayMarker>? markers = null,
        ReplayRenderMode renderMode = ReplayRenderMode.HostState,
        TimeProvider? timeProvider = null,
        ReviewSessionOptions? reviewOptions = null,
        Func<string, bool>? evidenceExists = null)
    {
        this.frames = frames ?? throw new ArgumentNullException(nameof(frames));
        baseDiagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        this.registerAnnotations = registerAnnotations;
        this.renderMode = renderMode;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.reviewOptions = reviewOptions ?? ReviewSessionOptions.Default;
        this.evidenceExists = evidenceExists ?? File.Exists;
        if (markers is not null) this.markers.AddRange(MaterializeMarkers(markers, nameof(markers), frames.Count));
        markerView = this.markers.AsReadOnly();
        logicalIndexBySourceId = BuildLogicalIndex();
        CurrentLogicalIndex = frames.Count == 0 ? -1 : 0;
        RebuildReviewSession();
        RefreshPresentation();
    }

    public IReadOnlyList<ITouchReplaySnapshot> Frames => frames;

    public IReadOnlyList<ReplayDiagnostic> BaseDiagnostics => baseDiagnostics;

    public IReadOnlyList<ReplayMarker> Markers => markerView;

    public ReviewSession ReviewSession => reviewSession;

    public ReviewSessionOptions ReviewOptions => reviewSession.Options;

    public long ReviewSessionRevision { get; private set; }

    /// <summary>
    /// Changes whenever diagnostics, markers, or exported human-review state changes.
    /// Filtering and selection do not affect report content and therefore leave this revision unchanged.
    /// </summary>
    public long ReportRevision { get; private set; }

    internal long NavigationIndexRevision { get; private set; }

    public ReviewQueueFilter Filter { get; private set; }

    public IReadOnlyList<ReviewEventGroup> VisibleGroups => visibleGroups;

    public int CurrentLogicalIndex { get; private set; }

    public ITouchReplaySnapshot? CurrentSnapshot =>
        CurrentLogicalIndex >= 0 ? frames[CurrentLogicalIndex] : null;

    public SourceRecord? CurrentSource => CurrentSnapshot?.PrimarySource;

    public InspectorFramePresentation? CurrentPresentation { get; private set; }

    public byte? SelectedContactId { get; private set; }

    public InspectorContactRow? SelectedContact => SelectedContactId is { } id
        ? CurrentPresentation?.Contacts.FirstOrDefault(contact => contact.Id == id)
        : null;

    public string? SelectedFindingGroupId { get; private set; }

    public string? SelectedOccurrenceId { get; private set; }

    public ReviewEventGroup? SelectedFinding => SelectedFindingGroupId is { } id &&
        visibleGroupsById.TryGetValue(id, out var group)
            ? group
            : null;

    public ReviewOccurrence? SelectedOccurrence => SelectedFinding?.Occurrences
        .FirstOrDefault(occurrence => occurrence.Id.Equals(SelectedOccurrenceId, StringComparison.Ordinal));

    public string? SelectedMarkerId { get; private set; }

    public ReplayMarker? SelectedMarker => SelectedMarkerId is { } id
        ? markers.FirstOrDefault(marker => marker.Id.Equals(id, StringComparison.Ordinal))
        : null;

    public InspectorFramePresentation SelectFrame(int logicalIndex)
    {
        if ((uint)logicalIndex >= (uint)frames.Count)
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        CurrentLogicalIndex = logicalIndex;
        RefreshPresentation();
        return CurrentPresentation!;
    }

    public void SetRenderMode(ReplayRenderMode value)
    {
        if (renderMode == value) return;
        renderMode = value;
        RefreshPresentation();
    }

    public bool SetReviewOptions(ReviewSessionOptions value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (reviewOptions == value && reviewSession.Options == value) return false;
        reviewOptions = value;
        reviewSession.Options = value;
        return true;
    }

    public bool SelectContact(byte contactId)
    {
        if (CurrentPresentation?.Contacts.Any(contact => contact.Id == contactId) != true)
        {
            SelectedContactId = null;
            return false;
        }
        SelectedContactId = contactId;
        return true;
    }

    public void ClearContactSelection() => SelectedContactId = null;

    public void SetFilter(ReviewQueueFilter value)
    {
        if (Filter == value) return;
        Filter = value;
        RefreshVisibleGroups();
    }

    public ReviewOccurrence SelectFinding(string groupId, int occurrenceIndex = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (!visibleGroupsById.TryGetValue(groupId, out var group))
            throw new KeyNotFoundException($"Review group '{groupId}' is not visible under filter {Filter}.");
        if ((uint)occurrenceIndex >= (uint)group.Occurrences.Count)
            throw new ArgumentOutOfRangeException(nameof(occurrenceIndex));
        return ApplyOccurrence(group, group.Occurrences[occurrenceIndex]);
    }

    public ReviewOccurrence SelectOccurrence(int occurrenceIndex)
    {
        var group = SelectedFinding ?? throw new InvalidOperationException("Select a review group first.");
        if ((uint)occurrenceIndex >= (uint)group.Occurrences.Count)
            throw new ArgumentOutOfRangeException(nameof(occurrenceIndex));
        return ApplyOccurrence(group, group.Occurrences[occurrenceIndex]);
    }

    public ReviewOccurrence? NavigateFinding(ReviewFindingDirection direction)
    {
        var candidates = navigableOccurrences;
        if (candidates.Length == 0) return null;

        var selectedIndex = SelectedOccurrenceId is { } occurrenceId &&
            navigablePositionByOccurrenceId.TryGetValue(occurrenceId, out var position)
                ? position
                : -1;
        int targetIndex;
        if (selectedIndex >= 0)
        {
            targetIndex = direction == ReviewFindingDirection.Next
                ? (selectedIndex + 1) % candidates.Length
                : (selectedIndex + candidates.Length - 1) % candidates.Length;
        }
        else if (direction == ReviewFindingDirection.Next)
        {
            targetIndex = FirstOccurrenceAfter(CurrentLogicalIndex);
            if (targetIndex == candidates.Length) targetIndex = 0;
        }
        else
        {
            targetIndex = FirstOccurrenceAtOrAfter(CurrentLogicalIndex) - 1;
            if (targetIndex < 0) targetIndex = candidates.Length - 1;
        }

        var target = candidates[targetIndex];
        return ApplyOccurrence(target.Group, target.Occurrence);
    }

    public void ClearFindingSelection()
    {
        SelectedFindingGroupId = null;
        SelectedOccurrenceId = null;
        SelectedMarkerId = null;
    }

    public ReviewEventGroup AcknowledgeFinding(string groupId) =>
        MutateFinding(groupId, reviewSession.Acknowledge);

    public ReviewEventGroup SetFindingDisposition(string groupId, ReviewDisposition disposition) =>
        MutateFinding(groupId, id => reviewSession.SetDisposition(id, disposition));

    public ReviewEventGroup ResolveFinding(string groupId) =>
        MutateFinding(groupId, reviewSession.Resolve);

    public ReplayMarker AddMarker(string? label = null)
    {
        if (CurrentLogicalIndex < 0)
            throw new InvalidOperationException("A marker requires a selected replay frame.");
        var normalizedLabel = string.IsNullOrWhiteSpace(label) ? NextMarkerLabel() : label.Trim();
        EnsureUniqueMarkerLabel(normalizedLabel, exceptMarkerId: null);
        string id;
        do
        {
            id = $"marker-{Guid.NewGuid():N}";
        }
        while (markers.Any(marker => marker.Id.Equals(id, StringComparison.Ordinal)));

        var marker = new ReplayMarker(
            id,
            normalizedLabel,
            CurrentLogicalIndex,
            CurrentLogicalIndex,
            timeProvider.GetUtcNow());
        markers.Add(marker);
        RebuildReviewSession($"ANNOTATION_MARKER:{marker.Id}");
        SelectedMarkerId = marker.Id;
        return marker;
    }

    public ReplayMarker RenameMarker(string markerId, string label)
    {
        var (marker, index) = FindMarker(markerId);
        var normalized = label?.Trim() ?? string.Empty;
        if (normalized.Length == 0) throw new ArgumentException("Marker name cannot be empty.", nameof(label));
        EnsureUniqueMarkerLabel(normalized, marker.Id);
        if (marker.Label.Equals(normalized, StringComparison.Ordinal))
        {
            SelectMarker(marker.Id);
            return marker;
        }
        var renamed = marker with { Label = normalized };
        markers[index] = renamed;
        RebuildReviewSession($"ANNOTATION_MARKER:{renamed.Id}");
        SelectedMarkerId = renamed.Id;
        return renamed;
    }

    public ReplayMarker SetMarkerQaCaseIds(string markerId, IEnumerable<string> qaCaseIds)
    {
        ArgumentNullException.ThrowIfNull(qaCaseIds);
        var normalized = qaCaseIds
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return UpdateMarker(markerId, marker => marker with { QaCaseIds = normalized });
    }

    public ReplayMarker AddMarkerEvidence(string markerId, ReplayEvidenceReference evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateEvidence(evidence, nameof(evidence));
        if (markers.SelectMany(marker => marker.Evidence ?? []).Any(item =>
                item.Id.Equals(evidence.Id, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Duplicate evidence ID '{evidence.Id}'.", nameof(evidence));
        }
        return UpdateMarker(markerId, marker => marker with
        {
            Evidence = (marker.Evidence ?? []).Append(evidence).ToArray(),
        });
    }

    public bool RemoveMarker(string markerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerId);
        var index = markers.FindIndex(marker => marker.Id.Equals(markerId, StringComparison.Ordinal));
        if (index < 0) return false;
        markers.RemoveAt(index);
        if (SelectedMarkerId?.Equals(markerId, StringComparison.Ordinal) == true ||
            SelectedFindingGroupId?.Equals($"ANNOTATION_MARKER:{markerId}", StringComparison.Ordinal) == true)
        {
            ClearFindingSelection();
        }
        RebuildReviewSession();
        return true;
    }

    public int ClearMarkers()
    {
        var count = markers.Count;
        if (count == 0) return 0;
        markers.Clear();
        if (SelectedFinding?.Category == ReviewEventCategory.Annotation || SelectedMarkerId is not null)
            ClearFindingSelection();
        RebuildReviewSession();
        return count;
    }

    public ReviewWorkspaceImportResult ReplaceMarkersAndImportState(
        IEnumerable<ReplayMarker> replacementMarkers,
        IEnumerable<ReviewStateSnapshot> reviewStates)
    {
        var replacement = MaterializeMarkers(replacementMarkers, nameof(replacementMarkers), frames.Count);
        ArgumentNullException.ThrowIfNull(reviewStates);
        var importedState = reviewStates.ToArray();
        if (importedState.Any(state => state is null))
            throw new ArgumentException("Review state collections cannot contain null entries.", nameof(reviewStates));
        var priorState = reviewSession.ExportState();
        var markersChanged = !MarkerSequencesEqual(markers, replacement);
        var replacementSession = CreateReviewSession(replacement);
        var unresolved = replacementSession.ImportState(importedState);
        var replacementState = replacementSession.ExportState();
        var reviewStateChanged = !StateSequencesEqual(priorState, replacementState);
        if (!markersChanged && !reviewStateChanged)
            return new ReviewWorkspaceImportResult(markers.Count, unresolved, false, false);

        var priorGroupId = SelectedFindingGroupId;
        var priorOccurrenceId = SelectedOccurrenceId;
        var priorMarkerId = SelectedMarkerId;
        if (markersChanged)
        {
            markers.Clear();
            markers.AddRange(replacement);
        }
        reviewSession = replacementSession;
        ReviewSessionRevision++;
        ReportRevision++;
        RefreshVisibleGroups();
        RestoreSelection(priorGroupId, priorOccurrenceId, priorMarkerId);
        return new ReviewWorkspaceImportResult(markers.Count, unresolved, markersChanged, reviewStateChanged);
    }

    public bool SelectMarker(string markerId)
    {
        if (!markers.Any(marker => marker.Id.Equals(markerId, StringComparison.Ordinal))) return false;
        SelectedMarkerId = markerId;
        var group = visibleGroups.FirstOrDefault(item =>
            item.Id.Equals($"ANNOTATION_MARKER:{markerId}", StringComparison.Ordinal));
        if (group is null)
        {
            SelectedFindingGroupId = null;
            SelectedOccurrenceId = null;
            return true;
        }
        ApplyOccurrence(group, group.Occurrences[0]);
        return true;
    }

    public string BuildCopyPayload() => CurrentPresentation is { } presentation
        ? InspectorPresentationBuilder.BuildCopyText(presentation)
        : string.Empty;

    private ReviewOccurrence ApplyOccurrence(ReviewEventGroup group, ReviewOccurrence occurrence)
    {
        SelectedFindingGroupId = group.Id;
        SelectedOccurrenceId = occurrence.Id;
        SelectedMarkerId = occurrence.Diagnostic.Details?.GetValueOrDefault("marker_id");
        if (occurrence.LogicalIndex is { } logicalIndex) SelectFrame(logicalIndex);
        return occurrence;
    }

    private ReviewEventGroup MutateFinding(
        string groupId,
        Func<string, ReviewEventGroup> mutation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        var previous = reviewSession.Find(groupId);
        var updated = mutation(groupId);
        if (previous.WorkflowState != updated.WorkflowState || previous.Disposition != updated.Disposition)
            ReportRevision++;
        RefreshVisibleGroups();
        return visibleGroups.FirstOrDefault(group => group.Id.Equals(groupId, StringComparison.Ordinal)) ?? updated;
    }

    private (ReplayMarker Marker, int Index) FindMarker(string markerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(markerId);
        var index = markers.FindIndex(marker => marker.Id.Equals(markerId, StringComparison.Ordinal));
        return index >= 0
            ? (markers[index], index)
            : throw new KeyNotFoundException($"Marker '{markerId}' was not found.");
    }

    private ReplayMarker UpdateMarker(string markerId, Func<ReplayMarker, ReplayMarker> update)
    {
        var (marker, index) = FindMarker(markerId);
        var updated = update(marker);
        if (!updated.Id.Equals(marker.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("Marker updates must preserve the stable marker ID.");
        if (MarkerEquals(marker, updated))
        {
            SelectMarker(marker.Id);
            return marker;
        }
        markers[index] = updated;
        RebuildReviewSession($"ANNOTATION_MARKER:{updated.Id}");
        SelectedMarkerId = updated.Id;
        return updated;
    }

    private void EnsureUniqueMarkerLabel(string label, string? exceptMarkerId)
    {
        if (markers.Any(marker =>
            !marker.Id.Equals(exceptMarkerId, StringComparison.Ordinal) &&
            marker.Label.Equals(label, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"Marker name already exists: {label}", nameof(label));
        }
    }

    private string NextMarkerLabel()
    {
        var number = 1;
        while (markers.Any(marker => marker.Label.Equals($"Marker {number}", StringComparison.OrdinalIgnoreCase)))
            number++;
        return $"Marker {number}";
    }

    private void RefreshPresentation()
    {
        if (CurrentSnapshot is not { } snapshot)
        {
            CurrentPresentation = null;
            SelectedContactId = null;
            return;
        }
        var source = snapshot.PrimarySource;
        CurrentPresentation = InspectorPresentationBuilder.Build(
            source,
            snapshot.DecodedFrame,
            snapshot,
            frames.Count,
            renderMode,
            registerAnnotations?.Find(source.StableId));
        if (SelectedContactId is { } id &&
            CurrentPresentation.Contacts.All(contact => contact.Id != id))
        {
            SelectedContactId = null;
        }
    }

    private IReadOnlyList<string> RebuildReviewSession(
        string? preferredGroupId = null)
    {
        var priorState = reviewSession?.ExportState() ?? [];
        if (reviewSession is not null) reviewOptions = reviewSession.Options;
        var priorGroupId = preferredGroupId ?? SelectedFindingGroupId;
        var priorOccurrenceId = SelectedOccurrenceId;
        var priorMarkerId = SelectedMarkerId;
        reviewSession = CreateReviewSession(markers);
        reviewSession.ImportState(priorState);
        ReviewSessionRevision++;
        ReportRevision++;
        RefreshVisibleGroups();
        RestoreSelection(priorGroupId, priorOccurrenceId, priorMarkerId);
        return [];
    }

    private ReviewSession CreateReviewSession(IReadOnlyList<ReplayMarker> sourceMarkers)
    {
        var markerDiagnostics = sourceMarkers.Select(MarkerDiagnostic).ToArray();
        return new ReviewSession(
            new CompositeReadOnlyList<ReplayDiagnostic>(baseDiagnostics, markerDiagnostics),
            logicalIndexBySourceId,
            reviewOptions);
    }

    private void RestoreSelection(string? groupId, string? occurrenceId, string? markerId)
    {
        SelectedFindingGroupId = null;
        SelectedOccurrenceId = null;
        SelectedMarkerId = markerId is not null &&
            markers.Any(marker => marker.Id.Equals(markerId, StringComparison.Ordinal))
                ? markerId
                : null;
        if (groupId is null || !visibleGroupsById.TryGetValue(groupId, out var group)) return;
        var occurrence = group.Occurrences.FirstOrDefault(item => item.Id.Equals(occurrenceId, StringComparison.Ordinal))
            ?? group.Occurrences[0];
        SelectedFindingGroupId = group.Id;
        SelectedOccurrenceId = occurrence.Id;
        SelectedMarkerId = occurrence.Diagnostic.Details?.GetValueOrDefault("marker_id");
    }

    private IReadOnlyDictionary<string, int> BuildLogicalIndex()
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var logicalIndex = 0; logicalIndex < frames.Count; logicalIndex++)
        {
            var snapshot = frames[logicalIndex];
            foreach (var source in snapshot.PhysicalRecords) result[source.StableId] = logicalIndex;
            result[snapshot.PrimarySource.StableId] = logicalIndex;
        }
        return result;
    }

    private ReplayDiagnostic MarkerDiagnostic(ReplayMarker marker)
    {
        var source = frames.Count == 0
            ? null
            : frames[Math.Clamp(marker.StartLogicalIndex, 0, frames.Count - 1)].PrimarySource;
        var evidence = marker.Evidence ?? [];
        return new ReplayDiagnostic(
            DiagnosticSeverity.Info,
            "ANNOTATION_MARKER",
            marker.IsRange
                ? $"{marker.Label} · frames {marker.StartLogicalIndex + 1}-{marker.EndLogicalIndex + 1}"
                : $"{marker.Label} · frame {marker.StartLogicalIndex + 1}",
            source?.StableId ?? baseDiagnostics.FirstOrDefault()?.SourceRecordId ?? "sidecar",
            source?.Location ?? baseDiagnostics.FirstOrDefault()?.Location ?? new SourceLocation(0, 0),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["marker_id"] = marker.Id,
                ["qa_case_ids"] = string.Join(", ", marker.QaCaseIds ?? []),
                ["evidence"] = evidence.Count == 0
                    ? "-"
                    : string.Join("; ", evidence.Select(item =>
                        $"{item.Kind}:{item.Label ?? Path.GetFileName(item.Path)}:{(evidenceExists(item.Path) ? "available" : "missing")}")),
            });
    }

    private static ReplayMarker[] MaterializeMarkers(
        IEnumerable<ReplayMarker> source,
        string parameterName,
        int frameCount)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var result = source.ToArray();
        if (result.Any(marker => marker is null))
            throw new ArgumentException("Marker collections cannot contain null entries.", parameterName);
        foreach (var marker in result)
        {
            if (string.IsNullOrWhiteSpace(marker.Id))
                throw new ArgumentException("Marker IDs cannot be empty.", parameterName);
            if (string.IsNullOrWhiteSpace(marker.Label))
                throw new ArgumentException($"Marker '{marker.Id}' has an empty label.", parameterName);
            if (marker.StartLogicalIndex != marker.EndLogicalIndex)
                throw new ArgumentException($"Marker '{marker.Id}' is a range; only single-frame markers are supported.", parameterName);
            if ((uint)marker.StartLogicalIndex >= (uint)frameCount)
                throw new ArgumentException($"Marker '{marker.Id}' points outside the replay frame range.", parameterName);
            ValidateQaCaseIds(marker, parameterName);
            foreach (var evidence in marker.Evidence ?? [])
            {
                if (evidence is null)
                    throw new ArgumentException($"Marker '{marker.Id}' contains null evidence.", parameterName);
                ValidateEvidence(evidence, parameterName);
            }
        }
        var duplicateMarkerId = result
            .GroupBy(marker => marker.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateMarkerId is not null)
            throw new ArgumentException($"Duplicate marker ID '{duplicateMarkerId}'.", parameterName);
        var duplicateMarkerLabel = result
            .GroupBy(marker => marker.Label.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateMarkerLabel is not null)
            throw new ArgumentException($"Duplicate marker label '{duplicateMarkerLabel}'.", parameterName);
        var duplicateEvidenceId = result
            .SelectMany(marker => marker.Evidence ?? [])
            .GroupBy(evidence => evidence.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateEvidenceId is not null)
            throw new ArgumentException($"Duplicate evidence ID '{duplicateEvidenceId}'.", parameterName);
        return result;
    }

    private static void ValidateQaCaseIds(ReplayMarker marker, string parameterName)
    {
        var values = marker.QaCaseIds ?? [];
        if (values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException($"Marker '{marker.Id}' contains an empty QA case ID.", parameterName);
        var duplicate = values
            .GroupBy(value => value.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw new ArgumentException($"Marker '{marker.Id}' contains duplicate QA case ID '{duplicate}'.", parameterName);
    }

    private static void ValidateEvidence(ReplayEvidenceReference evidence, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(evidence.Id))
            throw new ArgumentException("Evidence IDs cannot be empty.", parameterName);
        if (string.IsNullOrWhiteSpace(evidence.Path))
            throw new ArgumentException($"Evidence '{evidence.Id}' has an empty path.", parameterName);
        if (evidence.Label is not null && string.IsNullOrWhiteSpace(evidence.Label))
            throw new ArgumentException($"Evidence '{evidence.Id}' has an empty label.", parameterName);
        if (evidence.Sha256 is not null &&
            (evidence.Sha256.Length != 64 || evidence.Sha256.Any(value => !Uri.IsHexDigit(value))))
        {
            throw new ArgumentException($"Evidence '{evidence.Id}' has an invalid SHA-256 value.", parameterName);
        }
    }

    private static bool MarkerSequencesEqual(IReadOnlyList<ReplayMarker> left, IReadOnlyList<ReplayMarker> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => MarkerEquals(pair.First, pair.Second));

    private static bool MarkerEquals(ReplayMarker left, ReplayMarker right) =>
        left.Id.Equals(right.Id, StringComparison.Ordinal) &&
        left.Label.Equals(right.Label, StringComparison.Ordinal) &&
        left.StartLogicalIndex == right.StartLogicalIndex &&
        left.EndLogicalIndex == right.EndLogicalIndex &&
        left.CreatedAt == right.CreatedAt &&
        string.Equals(left.Note, right.Note, StringComparison.Ordinal) &&
        (left.QaCaseIds ?? []).SequenceEqual(right.QaCaseIds ?? [], StringComparer.Ordinal) &&
        (left.Evidence ?? []).SequenceEqual(right.Evidence ?? []);

    private static bool StateSequencesEqual(
        IReadOnlyList<ReviewStateSnapshot> left,
        IReadOnlyList<ReviewStateSnapshot> right) => left.SequenceEqual(right);

    private void RefreshVisibleGroups()
    {
        visibleGroups = reviewSession.Filter(Filter)
            .Where(group =>
                group.Severity >= DiagnosticSeverity.Warning ||
                group.IsAsil ||
                group.Category is ReviewEventCategory.QaResult or ReviewEventCategory.Annotation)
            .ToArray();
        visibleGroupsById = visibleGroups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        navigableOccurrences = visibleGroups
            .SelectMany(group => group.Occurrences.Select((occurrence, index) =>
                new NavigableOccurrence(group, occurrence, index)))
            .Where(item => item.Occurrence.LogicalIndex is not null)
            .OrderBy(item => item.Occurrence.LogicalIndex)
            .ThenBy(item => item.Group.Id, StringComparer.Ordinal)
            .ThenBy(item => item.OccurrenceIndex)
            .ToArray();
        navigablePositionByOccurrenceId = navigableOccurrences
            .Select((item, index) => (item.Occurrence.Id, Index: index))
            .ToDictionary(item => item.Id, item => item.Index, StringComparer.Ordinal);
        NavigationIndexRevision++;
        if (SelectedFindingGroupId is not { } id ||
            visibleGroupsById.ContainsKey(id)) return;
        ClearFindingSelection();
    }

    private int FirstOccurrenceAfter(int logicalIndex)
    {
        var low = 0;
        var high = navigableOccurrences.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (navigableOccurrences[middle].Occurrence.LogicalIndex!.Value <= logicalIndex) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private int FirstOccurrenceAtOrAfter(int logicalIndex)
    {
        var low = 0;
        var high = navigableOccurrences.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (navigableOccurrences[middle].Occurrence.LogicalIndex!.Value < logicalIndex) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private sealed record NavigableOccurrence(
        ReviewEventGroup Group,
        ReviewOccurrence Occurrence,
        int OccurrenceIndex);

    private sealed class CompositeReadOnlyList<T>(IReadOnlyList<T> first, IReadOnlyList<T> second)
        : IReadOnlyList<T>
    {
        public int Count => first.Count + second.Count;

        public T this[int index] => index < first.Count ? first[index] : second[index - first.Count];

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in first) yield return item;
            foreach (var item in second) yield return item;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
