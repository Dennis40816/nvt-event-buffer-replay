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
        if (markers is not null) this.markers.AddRange(MaterializeMarkers(markers, nameof(markers)));
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

    public ReviewEventGroup? SelectedFinding => SelectedFindingGroupId is { } id
        ? visibleGroups.FirstOrDefault(group => group.Id.Equals(id, StringComparison.Ordinal))
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
        var group = visibleGroups.FirstOrDefault(item => item.Id.Equals(groupId, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Review group '{groupId}' is not visible under filter {Filter}.");
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
        var candidates = NavigableOccurrences();
        if (candidates.Length == 0) return null;

        var selectedIndex = Array.FindIndex(candidates, item =>
            item.Occurrence.Id.Equals(SelectedOccurrenceId, StringComparison.Ordinal));
        int targetIndex;
        if (selectedIndex >= 0)
        {
            targetIndex = direction == ReviewFindingDirection.Next
                ? (selectedIndex + 1) % candidates.Length
                : (selectedIndex + candidates.Length - 1) % candidates.Length;
        }
        else if (direction == ReviewFindingDirection.Next)
        {
            targetIndex = Array.FindIndex(candidates, item => item.Occurrence.LogicalIndex > CurrentLogicalIndex);
            if (targetIndex < 0) targetIndex = 0;
        }
        else
        {
            targetIndex = Array.FindLastIndex(candidates, item => item.Occurrence.LogicalIndex < CurrentLogicalIndex);
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
        var replacement = MaterializeMarkers(replacementMarkers, nameof(replacementMarkers));
        ArgumentNullException.ThrowIfNull(reviewStates);
        var importedState = reviewStates.ToArray();
        var priorState = reviewSession.ExportState();
        var markersChanged = !MarkerSequencesEqual(markers, replacement);
        IReadOnlyList<string> unresolved;

        if (markersChanged)
        {
            markers.Clear();
            markers.AddRange(replacement);
            if (SelectedMarkerId is { } markerId &&
                markers.All(marker => !marker.Id.Equals(markerId, StringComparison.Ordinal)))
            {
                SelectedMarkerId = null;
            }
            unresolved = RebuildReviewSession(importState: importedState);
        }
        else
        {
            unresolved = reviewSession.ImportState(importedState);
            if (!StateSequencesEqual(priorState, reviewSession.ExportState())) RefreshVisibleGroups();
        }

        var reviewStateChanged = !StateSequencesEqual(priorState, reviewSession.ExportState());
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
        string? preferredGroupId = null,
        IEnumerable<ReviewStateSnapshot>? importState = null)
    {
        var priorState = reviewSession?.ExportState() ?? [];
        if (reviewSession is not null) reviewOptions = reviewSession.Options;
        var priorGroupId = preferredGroupId ?? SelectedFindingGroupId;
        var priorOccurrenceId = SelectedOccurrenceId;
        var markerDiagnostics = markers.Select(MarkerDiagnostic).ToArray();
        reviewSession = new ReviewSession(
            new CompositeReadOnlyList<ReplayDiagnostic>(baseDiagnostics, markerDiagnostics),
            logicalIndexBySourceId,
            reviewOptions);
        reviewSession.ImportState(priorState);
        var unresolved = importState is null ? [] : reviewSession.ImportState(importState);
        ReviewSessionRevision++;
        RefreshVisibleGroups();
        if (priorGroupId is null) return unresolved;
        var group = visibleGroups.FirstOrDefault(item => item.Id.Equals(priorGroupId, StringComparison.Ordinal));
        if (group is null)
        {
            ClearFindingSelection();
            return unresolved;
        }
        var occurrence = group.Occurrences.FirstOrDefault(item => item.Id.Equals(priorOccurrenceId, StringComparison.Ordinal))
            ?? group.Occurrences[0];
        SelectedFindingGroupId = group.Id;
        SelectedOccurrenceId = occurrence.Id;
        SelectedMarkerId = occurrence.Diagnostic.Details?.GetValueOrDefault("marker_id");
        return unresolved;
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

    private static ReplayMarker[] MaterializeMarkers(IEnumerable<ReplayMarker> source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        var result = source.ToArray();
        if (result.Any(marker => marker is null))
            throw new ArgumentException("Marker collections cannot contain null entries.", parameterName);
        var duplicateMarkerId = result
            .GroupBy(marker => marker.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateMarkerId is not null)
            throw new ArgumentException($"Duplicate marker ID '{duplicateMarkerId}'.", parameterName);
        var duplicateEvidenceId = result
            .SelectMany(marker => marker.Evidence ?? [])
            .GroupBy(evidence => evidence.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicateEvidenceId is not null)
            throw new ArgumentException($"Duplicate evidence ID '{duplicateEvidenceId}'.", parameterName);
        return result;
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
        if (SelectedFindingGroupId is not { } id ||
            visibleGroups.Any(group => group.Id.Equals(id, StringComparison.Ordinal))) return;
        ClearFindingSelection();
    }

    private NavigableOccurrence[] NavigableOccurrences() => visibleGroups
        .SelectMany(group => group.Occurrences.Select((occurrence, index) =>
            new NavigableOccurrence(group, occurrence, index)))
        .Where(item => item.Occurrence.LogicalIndex is not null)
        .OrderBy(item => item.Occurrence.LogicalIndex)
        .ThenBy(item => item.Group.Id, StringComparer.Ordinal)
        .ThenBy(item => item.OccurrenceIndex)
        .ToArray();

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
