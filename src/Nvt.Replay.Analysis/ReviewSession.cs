using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

/// <summary>Classifies review occurrences by the kind of diagnostic evidence.</summary>
public enum ReviewEventCategory
{
    /// <summary>Identifies diagnostics outside the QA, annotation, device-state, and behavior categories.</summary>
    ProtocolDiagnostic,
    /// <summary>Identifies ASIL, state, and NVT sample diagnostics.</summary>
    DeviceState,
    /// <summary>Identifies capture or palm behavior diagnostics.</summary>
    BehaviorFinding,
    /// <summary>Identifies diagnostics whose codes begin with QA_.</summary>
    QaResult,
    /// <summary>Identifies diagnostics whose codes begin with ANNOTATION_.</summary>
    Annotation,
}

/// <summary>Describes the alarm transition derived from a captured diagnostic.</summary>
public enum CapturedAlarmState
{
    /// <summary>Indicates that the diagnostic has no captured alarm transition.</summary>
    NotApplicable,
    /// <summary>Indicates an alarm-severity diagnostic whose code contains ASIL.</summary>
    Asserted,
    /// <summary>Indicates a diagnostic whose code ends with _CLEARED.</summary>
    Cleared,
}

/// <summary>Tracks the human review progress of an event group.</summary>
public enum ReviewWorkflowState
{
    /// <summary>Indicates a review group with no recorded acknowledgement or resolution.</summary>
    Open,
    /// <summary>Indicates that a review group has been acknowledged.</summary>
    Acknowledged,
    /// <summary>Indicates that a review group has been resolved.</summary>
    Resolved,
}

/// <summary>Records the human-assigned disposition of a review event group.</summary>
public enum ReviewDisposition
{
    /// <summary>Indicates that no disposition has been assigned to a review group.</summary>
    None,
    /// <summary>Labels a review group as expected.</summary>
    Expected,
    /// <summary>Labels a review group as a false positive.</summary>
    FalsePositive,
}

/// <summary>Combines captured alarm state and human workflow state for an ASIL review group.</summary>
public enum AsilLifecycleState
{
    /// <summary>Indicates an unresolved ASIL group that is neither cleared nor acknowledged.</summary>
    Asserted,
    /// <summary>Indicates an acknowledged ASIL group whose captured alarm state is not cleared.</summary>
    Acknowledged,
    /// <summary>Indicates an unresolved ASIL group whose captured alarm state is cleared.</summary>
    Cleared,
    /// <summary>Indicates a resolved ASIL review group.</summary>
    Resolved,
}

/// <summary>Selects review groups by severity or the presence of ASIL diagnostics.</summary>
public enum ReviewQueueFilter
{
    /// <summary>Selects all review event groups.</summary>
    All,
    /// <summary>Selects review groups whose highest diagnostic severity is Warning or Error.</summary>
    Warnings,
    /// <summary>Selects review groups whose highest diagnostic severity is Alarm.</summary>
    Alarms,
    /// <summary>Selects review groups containing a diagnostic code with ASIL, ignoring case.</summary>
    Asil,
}

/// <summary>Associates a captured diagnostic with a review category, alarm transition, and optional logical replay index.</summary>
/// <param name="Id">The identifier formed from the diagnostic code, source record ID, and diagnostic index.</param>
/// <param name="Diagnostic">The captured diagnostic represented by this occurrence.</param>
/// <param name="Category">The category derived from the diagnostic code.</param>
/// <param name="CapturedAlarmState">The alarm transition derived from the diagnostic code and severity.</param>
/// <param name="LogicalIndex">The mapped logical replay index, or null when no nonnegative source index is available.</param>
public sealed record ReviewOccurrence(
    string Id,
    ReplayDiagnostic Diagnostic,
    ReviewEventCategory Category,
    CapturedAlarmState CapturedAlarmState,
    int? LogicalIndex);

/// <summary>Combines correlated diagnostic occurrences with their captured alarm state and human review state.</summary>
/// <param name="Id">The group identifier, equal to its correlation key.</param>
/// <param name="CorrelationKey">The grouping key derived from the diagnostic code and applicable marker or counter details.</param>
/// <param name="Code">The diagnostic code of the first occurrence in the group.</param>
/// <param name="Message">The diagnostic message of the first occurrence in the group.</param>
/// <param name="Category">The review category of the first occurrence in the group.</param>
/// <param name="Severity">The highest diagnostic severity among the occurrences.</param>
/// <param name="Occurrences">The correlated diagnostic occurrences in their original order.</param>
/// <param name="CurrentCapturedAlarmState">The last applicable captured alarm state, or NotApplicable if none exists.</param>
/// <param name="WorkflowState">The human review progress recorded for the group.</param>
/// <param name="Disposition">The human-assigned disposition recorded for the group.</param>
/// <param name="AsilLifecycle">The combined captured and review state for an ASIL group, or null for other groups.</param>
public sealed record ReviewEventGroup(
    string Id,
    string CorrelationKey,
    string Code,
    string Message,
    ReviewEventCategory Category,
    DiagnosticSeverity Severity,
    IReadOnlyList<ReviewOccurrence> Occurrences,
    CapturedAlarmState CurrentCapturedAlarmState,
    ReviewWorkflowState WorkflowState,
    ReviewDisposition Disposition,
    AsilLifecycleState? AsilLifecycle)
{
    /// <summary>Indicates whether any occurrence has a diagnostic code containing ASIL, ignoring case.</summary>
    public bool IsAsil => Occurrences.Any(item => IsAsilCode(item.Diagnostic.Code));
    /// <summary>Gets the smallest nonnegative logical replay index among the occurrences, or null if none exists.</summary>
    public int? FirstLogicalIndex => Occurrences.Select(item => item.LogicalIndex).Where(item => item is not null).Cast<int>().DefaultIfEmpty(-1).Min() is var value && value >= 0 ? value : null;

    private static bool IsAsilCode(string code) => code.Contains("ASIL", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Specifies whether replay pauses at alarm diagnostics or QA failure diagnostics.</summary>
/// <param name="PauseOnAlarm">Whether replay should pause at occurrences with Alarm severity.</param>
/// <param name="PauseOnQaFail">Whether replay should pause at QA diagnostics with FAIL in the code or a fail result detail.</param>
public sealed record ReviewSessionOptions(bool PauseOnAlarm, bool PauseOnQaFail)
{
    /// <summary>Gets options that enable pausing for both alarm diagnostics and QA failures.</summary>
    public static ReviewSessionOptions Default { get; } = new(true, true);
}

/// <summary>Stores the human workflow state and disposition for a review event group.</summary>
/// <param name="GroupId">The identifier of the review event group.</param>
/// <param name="WorkflowState">The human review progress recorded for the group.</param>
/// <param name="Disposition">The human-assigned disposition recorded for the group.</param>
public sealed record ReviewStateSnapshot(
    string GroupId,
    ReviewWorkflowState WorkflowState,
    ReviewDisposition Disposition);

/// <summary>Groups captured diagnostics for human review and identifies logical replay indices that should pause.</summary>
public sealed class ReviewSession
{
    private readonly IReadOnlyList<ReplayDiagnostic> diagnostics;
    private readonly IReadOnlyList<ReviewOccurrence> occurrences;
    private readonly int[] alarmPauseIndices;
    private readonly int[] qaFailPauseIndices;
    private readonly IReadOnlyDictionary<string, ReplayDiagnostic[]> alertsBySourceId;
    private readonly IReadOnlyDictionary<int, ReplayDiagnostic[]> alertsByLineNumber;
    private readonly Dictionary<string, HumanReviewState> humanState = new(StringComparer.Ordinal);

    /// <summary>Creates a review session from diagnostics, optional source-to-logical indices, and pause options.</summary>
    public ReviewSession(
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyDictionary<string, int>? logicalIndexBySourceId = null,
        ReviewSessionOptions? options = null)
    {
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        var sourceIndices = logicalIndexBySourceId ?? new Dictionary<string, int>();
        Options = options ?? ReviewSessionOptions.Default;
        occurrences = diagnostics.Select((diagnostic, index) =>
            new ReviewOccurrence(
                $"{diagnostic.Code}@{diagnostic.SourceRecordId}#{index}",
                diagnostic,
                Category(diagnostic),
                CapturedState(diagnostic),
                sourceIndices.GetValueOrDefault(diagnostic.SourceRecordId, -1) is var logicalIndex && logicalIndex >= 0
                    ? logicalIndex
                    : null))
            .ToArray();
        alarmPauseIndices = PauseIndices(item => item.Diagnostic.Severity == DiagnosticSeverity.Alarm);
        qaFailPauseIndices = PauseIndices(item => item.Category == ReviewEventCategory.QaResult && IsQaFail(item.Diagnostic));
        HasNavigableFindings = occurrences.Any(item => item.LogicalIndex is not null);
        var alerts = diagnostics.Where(item => item.Severity >= DiagnosticSeverity.Warning).ToArray();
        alertsBySourceId = alerts
            .GroupBy(item => item.SourceRecordId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        alertsByLineNumber = alerts
            .GroupBy(item => item.Location.LineNumber)
            .ToDictionary(group => group.Key, group => group.ToArray());
    }

    /// <summary>Gets or sets the options that control pausing at alarm and QA failure occurrences.</summary>
    public ReviewSessionOptions Options { get; set; }

    /// <summary>Gets the diagnostics supplied to this review session.</summary>
    public IReadOnlyList<ReplayDiagnostic> Diagnostics => diagnostics;

    /// <summary>Indicates whether any diagnostic occurrence has a mapped logical replay index.</summary>
    public bool HasNavigableFindings { get; }

    /// <summary>Returns distinct diagnostics of Warning severity or higher matching the source ID or line, up to the limit.</summary>
    public IReadOnlyList<ReplayDiagnostic> FrameAlerts(string sourceRecordId, int lineNumber, int maximum = 2)
    {
        if (maximum <= 0) return [];
        var bySource = alertsBySourceId.TryGetValue(sourceRecordId, out var sourceAlerts)
            ? sourceAlerts
            : Array.Empty<ReplayDiagnostic>();
        var byLine = alertsByLineNumber.TryGetValue(lineNumber, out var lineAlerts)
            ? lineAlerts
            : Array.Empty<ReplayDiagnostic>();
        return bySource
            .Concat(byLine)
            .Distinct()
            .Take(maximum)
            .ToArray();
    }

    /// <summary>Gets all review groups ordered by resolution state, severity, first logical index, and code.</summary>
    public IReadOnlyList<ReviewEventGroup> Groups => BuildGroups(ReviewQueueFilter.All);

    /// <summary>Returns matching review groups ordered by resolution state, severity, first logical index, and code.</summary>
    public IReadOnlyList<ReviewEventGroup> Filter(ReviewQueueFilter filter) => BuildGroups(filter);

    /// <summary>Exports recorded human review states ordered by group identifier.</summary>
    public IReadOnlyList<ReviewStateSnapshot> ExportState() => humanState
        .OrderBy(item => item.Key, StringComparer.Ordinal)
        .Select(item => new ReviewStateSnapshot(item.Key, item.Value.WorkflowState, item.Value.Disposition))
        .ToArray();

    /// <summary>Applies human review states to known groups and returns identifiers of groups that could not be matched.</summary>
    public IReadOnlyList<string> ImportState(IEnumerable<ReviewStateSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var known = Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        var unresolved = new List<string>();
        foreach (var snapshot in snapshots)
        {
            if (!known.Contains(snapshot.GroupId))
            {
                unresolved.Add(snapshot.GroupId);
                continue;
            }
            humanState[snapshot.GroupId] = new HumanReviewState(snapshot.WorkflowState, snapshot.Disposition);
        }
        return unresolved;
    }

    /// <summary>Indicates whether enabled alarm or QA failure pause rules match the logical replay index.</summary>
    public bool ShouldPauseAt(int logicalIndex) =>
        Options.PauseOnAlarm && Array.BinarySearch(alarmPauseIndices, logicalIndex) >= 0 ||
        Options.PauseOnQaFail && Array.BinarySearch(qaFailPauseIndices, logicalIndex) >= 0;

    /// <summary>Returns the earliest enabled pause index after the start and through the end, or null when none exists.</summary>
    public int? FirstPauseIndex(int exclusiveStart, int inclusiveEnd)
    {
        var alarm = Options.PauseOnAlarm ? FirstIndexInRange(alarmPauseIndices, exclusiveStart, inclusiveEnd) : null;
        var qaFail = Options.PauseOnQaFail ? FirstIndexInRange(qaFailPauseIndices, exclusiveStart, inclusiveEnd) : null;
        return alarm is null ? qaFail : qaFail is null ? alarm : Math.Min(alarm.Value, qaFail.Value);
    }

    /// <summary>Marks a review group as acknowledged and returns the updated group.</summary>
    public ReviewEventGroup Acknowledge(string groupId)
    {
        var state = GetState(groupId);
        humanState[groupId] = state with { WorkflowState = ReviewWorkflowState.Acknowledged };
        return Find(groupId);
    }

    /// <summary>Sets a review group's human disposition and returns the updated group.</summary>
    public ReviewEventGroup SetDisposition(string groupId, ReviewDisposition disposition)
    {
        var state = GetState(groupId);
        humanState[groupId] = state with { Disposition = disposition };
        return Find(groupId);
    }

    /// <summary>Resolves an acknowledged group, requiring a cleared captured state for ASIL groups, and returns it.</summary>
    public ReviewEventGroup Resolve(string groupId)
    {
        var group = Find(groupId);
        if (group.WorkflowState != ReviewWorkflowState.Acknowledged)
            throw new InvalidOperationException("A review event must be acknowledged before it can be resolved.");
        if (group.AsilLifecycle is not null && group.CurrentCapturedAlarmState != CapturedAlarmState.Cleared)
            throw new InvalidOperationException("An ASIL alarm cannot be resolved while the captured state remains asserted.");
        humanState[groupId] = GetState(groupId) with { WorkflowState = ReviewWorkflowState.Resolved };
        return Find(groupId);
    }

    /// <summary>Returns the single review group with the specified identifier.</summary>
    public ReviewEventGroup Find(string groupId) =>
        Groups.Single(group => group.Id.Equals(groupId, StringComparison.Ordinal));

    private IReadOnlyList<ReviewEventGroup> BuildGroups(ReviewQueueFilter filter)
    {
        return BuildOccurrences()
            .GroupBy(item => CorrelationKey(item.Diagnostic), StringComparer.Ordinal)
            .Select(ToGroup)
            .Where(group => Matches(group, filter))
            .OrderBy(group => group.WorkflowState == ReviewWorkflowState.Resolved)
            .ThenByDescending(group => SeverityRank(group.Severity))
            .ThenBy(group => group.FirstLogicalIndex ?? int.MaxValue)
            .ThenBy(group => group.Code, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<ReviewOccurrence> BuildOccurrences() => occurrences;

    private int[] PauseIndices(Func<ReviewOccurrence, bool> predicate) => occurrences
        .Where(predicate)
        .Select(item => item.LogicalIndex)
        .Where(item => item is not null)
        .Select(item => item!.Value)
        .Distinct()
        .OrderBy(item => item)
        .ToArray();

    private static int? FirstIndexInRange(int[] sortedIndices, int exclusiveStart, int inclusiveEnd)
    {
        var position = Array.BinarySearch(sortedIndices, exclusiveStart + 1);
        if (position < 0) position = ~position;
        return position < sortedIndices.Length && sortedIndices[position] <= inclusiveEnd
            ? sortedIndices[position]
            : null;
    }

    private ReviewEventGroup ToGroup(IGrouping<string, ReviewOccurrence> grouped)
    {
        var occurrences = grouped.ToArray();
        var representative = occurrences[0].Diagnostic;
        var id = grouped.Key;
        var state = GetState(id);
        var currentCaptured = occurrences.Select(item => item.CapturedAlarmState).LastOrDefault(item => item != CapturedAlarmState.NotApplicable);
        var isAsil = occurrences.Any(item => item.Diagnostic.Code.Contains("ASIL", StringComparison.OrdinalIgnoreCase));
        AsilLifecycleState? lifecycle = isAsil
            ? state.WorkflowState == ReviewWorkflowState.Resolved ? AsilLifecycleState.Resolved
            : currentCaptured == CapturedAlarmState.Cleared ? AsilLifecycleState.Cleared
            : state.WorkflowState == ReviewWorkflowState.Acknowledged ? AsilLifecycleState.Acknowledged
            : AsilLifecycleState.Asserted
            : null;
        return new ReviewEventGroup(
            id,
            grouped.Key,
            representative.Code,
            representative.Message,
            occurrences[0].Category,
            occurrences.Max(item => item.Diagnostic.Severity),
            occurrences,
            currentCaptured,
            state.WorkflowState,
            state.Disposition,
            lifecycle);
    }

    private HumanReviewState GetState(string groupId) =>
        humanState.GetValueOrDefault(groupId, HumanReviewState.Default);

    private static string CorrelationKey(ReplayDiagnostic diagnostic)
    {
        if (diagnostic.Code is "COMMON_ASIL_ALARM" or "COMMON_ASIL_CLEARED") return "COMMON_ASIL";
        if (diagnostic.Code.StartsWith("ANNOTATION_", StringComparison.Ordinal) && diagnostic.Details?.GetValueOrDefault("marker_id") is { } markerId)
            return $"{diagnostic.Code}:{markerId}";
        if (diagnostic.Code == "COMMON_BUS_COUNTER_INCREASED" && diagnostic.Details?.GetValueOrDefault("counter") is { } counter)
            return $"{diagnostic.Code}:{counter}";
        return diagnostic.Code;
    }

    private static ReviewEventCategory Category(ReplayDiagnostic diagnostic)
    {
        if (diagnostic.Code.StartsWith("QA_", StringComparison.Ordinal)) return ReviewEventCategory.QaResult;
        if (diagnostic.Code.StartsWith("ANNOTATION_", StringComparison.Ordinal)) return ReviewEventCategory.Annotation;
        if (diagnostic.Code.Contains("ASIL", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Code.EndsWith("_STATE", StringComparison.Ordinal) ||
            diagnostic.Code.StartsWith("NVT_", StringComparison.Ordinal) && diagnostic.Code.EndsWith("_SAMPLE", StringComparison.Ordinal))
            return ReviewEventCategory.DeviceState;
        if (diagnostic.Code.StartsWith("CAPTURE_", StringComparison.Ordinal) || diagnostic.Code.Contains("PALM", StringComparison.Ordinal)) return ReviewEventCategory.BehaviorFinding;
        return ReviewEventCategory.ProtocolDiagnostic;
    }

    private static CapturedAlarmState CapturedState(ReplayDiagnostic diagnostic)
    {
        if (diagnostic.Code.EndsWith("_CLEARED", StringComparison.Ordinal)) return CapturedAlarmState.Cleared;
        if (diagnostic.Severity == DiagnosticSeverity.Alarm && diagnostic.Code.Contains("ASIL", StringComparison.OrdinalIgnoreCase)) return CapturedAlarmState.Asserted;
        return CapturedAlarmState.NotApplicable;
    }

    private static bool Matches(ReviewEventGroup group, ReviewQueueFilter filter) => filter switch
    {
        ReviewQueueFilter.All => true,
        ReviewQueueFilter.Warnings => group.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error,
        ReviewQueueFilter.Alarms => group.Severity == DiagnosticSeverity.Alarm,
        ReviewQueueFilter.Asil => group.IsAsil,
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private static int SeverityRank(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Alarm => 4,
        DiagnosticSeverity.Error => 3,
        DiagnosticSeverity.Warning => 2,
        _ => 1,
    };

    private static bool IsQaFail(ReplayDiagnostic diagnostic) =>
        diagnostic.Code.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(diagnostic.Details?.GetValueOrDefault("result"), "fail", StringComparison.OrdinalIgnoreCase);

    private sealed record HumanReviewState(ReviewWorkflowState WorkflowState, ReviewDisposition Disposition)
    {
        public static HumanReviewState Default { get; } = new(ReviewWorkflowState.Open, ReviewDisposition.None);
    }
}
