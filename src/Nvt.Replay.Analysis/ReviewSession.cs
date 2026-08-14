using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

public enum ReviewEventCategory
{
    ProtocolDiagnostic,
    DeviceState,
    BehaviorFinding,
    QaResult,
    Annotation,
}

public enum CapturedAlarmState
{
    NotApplicable,
    Asserted,
    Cleared,
}

public enum ReviewWorkflowState
{
    Open,
    Acknowledged,
    Resolved,
}

public enum ReviewDisposition
{
    None,
    Expected,
    FalsePositive,
}

public enum AsilLifecycleState
{
    Asserted,
    Acknowledged,
    Cleared,
    Resolved,
}

public enum ReviewQueueFilter
{
    All,
    Warnings,
    Alarms,
    Asil,
}

public sealed record ReviewOccurrence(
    string Id,
    ReplayDiagnostic Diagnostic,
    ReviewEventCategory Category,
    CapturedAlarmState CapturedAlarmState,
    int? LogicalIndex);

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
    public bool IsAsil => Occurrences.Any(item => IsAsilCode(item.Diagnostic.Code));
    public int? FirstLogicalIndex => Occurrences.Select(item => item.LogicalIndex).Where(item => item is not null).Cast<int>().DefaultIfEmpty(-1).Min() is var value && value >= 0 ? value : null;

    private static bool IsAsilCode(string code) => code.Contains("ASIL", StringComparison.OrdinalIgnoreCase);
}

public sealed record ReviewSessionOptions(bool PauseOnAlarm, bool PauseOnQaFail)
{
    public static ReviewSessionOptions Default { get; } = new(true, true);
}

public sealed class ReviewSession
{
    private readonly IReadOnlyList<ReplayDiagnostic> diagnostics;
    private readonly IReadOnlyDictionary<string, int> logicalIndexBySourceId;
    private readonly Dictionary<string, HumanReviewState> humanState = new(StringComparer.Ordinal);

    public ReviewSession(
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyDictionary<string, int>? logicalIndexBySourceId = null,
        ReviewSessionOptions? options = null)
    {
        this.diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        this.logicalIndexBySourceId = logicalIndexBySourceId ?? new Dictionary<string, int>();
        Options = options ?? ReviewSessionOptions.Default;
    }

    public ReviewSessionOptions Options { get; set; }

    public IReadOnlyList<ReplayDiagnostic> Diagnostics => diagnostics;

    public IReadOnlyList<ReviewEventGroup> Groups => BuildGroups(ReviewQueueFilter.All);

    public IReadOnlyList<ReviewEventGroup> Filter(ReviewQueueFilter filter) => BuildGroups(filter);

    public bool ShouldPauseAt(int logicalIndex) => BuildOccurrences().Any(item =>
        item.LogicalIndex == logicalIndex &&
        (Options.PauseOnAlarm && item.Diagnostic.Severity == DiagnosticSeverity.Alarm ||
         Options.PauseOnQaFail && item.Category == ReviewEventCategory.QaResult && IsQaFail(item.Diagnostic)));

    public int? FirstPauseIndex(int exclusiveStart, int inclusiveEnd) => BuildOccurrences()
        .Where(item => item.LogicalIndex is { } index && index > exclusiveStart && index <= inclusiveEnd)
        .Where(item => Options.PauseOnAlarm && item.Diagnostic.Severity == DiagnosticSeverity.Alarm ||
                       Options.PauseOnQaFail && item.Category == ReviewEventCategory.QaResult && IsQaFail(item.Diagnostic))
        .Select(item => item.LogicalIndex!.Value)
        .DefaultIfEmpty(-1)
        .Min() is var result && result >= 0 ? result : null;

    public ReviewEventGroup Acknowledge(string groupId)
    {
        var state = GetState(groupId);
        humanState[groupId] = state with { WorkflowState = ReviewWorkflowState.Acknowledged };
        return Find(groupId);
    }

    public ReviewEventGroup SetDisposition(string groupId, ReviewDisposition disposition)
    {
        var state = GetState(groupId);
        humanState[groupId] = state with { Disposition = disposition };
        return Find(groupId);
    }

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

    private IReadOnlyList<ReviewOccurrence> BuildOccurrences() => diagnostics.Select((diagnostic, index) =>
        new ReviewOccurrence(
            $"{diagnostic.Code}@{diagnostic.SourceRecordId}#{index}",
            diagnostic,
            Category(diagnostic),
            CapturedState(diagnostic),
            logicalIndexBySourceId.GetValueOrDefault(diagnostic.SourceRecordId, -1) is var logicalIndex && logicalIndex >= 0 ? logicalIndex : null))
        .ToArray();

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
