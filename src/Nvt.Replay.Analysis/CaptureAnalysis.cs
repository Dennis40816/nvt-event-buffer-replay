using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

/// <summary>Defines the inclusive logical frame indices selected for capture analysis.</summary>
/// <param name="StartLogicalIndex">The first logical frame index included in the analysis.</param>
/// <param name="EndLogicalIndex">The last logical frame index included in the analysis.</param>
public sealed record AnalysisRange(int StartLogicalIndex, int EndLogicalIndex);

/// <summary>Summarizes captured and frame timing, synthetic timestamps, and compressed idle gaps in the selected range.</summary>
/// <param name="Domain">The timestamp domain: captured, synthetic-frame, or mixed.</param>
/// <param name="CapturedDuration">The elapsed captured time between the first and last selected frames.</param>
/// <param name="FrameDuration">The elapsed frame time between the first and last selected frames.</param>
/// <param name="SyntheticTimestampFrames">The number of selected frames whose timestamps are synthetic.</param>
/// <param name="CompressedIdleGaps">The number of selected timeline entries with compressed idle gaps.</param>
public sealed record AnalysisClockSummary(
    string Domain,
    TimeSpan CapturedDuration,
    TimeSpan FrameDuration,
    int SyntheticTimestampFrames,
    int CompressedIdleGaps);

/// <summary>Holds a reported contact's identifier, type, status, coordinates, and invalid flag for analysis.</summary>
/// <param name="Id">The reported contact identifier.</param>
/// <param name="Type">The reported touch type of the contact.</param>
/// <param name="Status">The reported touch status of the contact.</param>
/// <param name="X">The contact's reported X coordinate.</param>
/// <param name="Y">The contact's reported Y coordinate.</param>
/// <param name="Invalid">Whether the contact is marked invalid.</param>
public sealed record AnalysisContact(
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    bool Invalid);

/// <summary>Determines which reported contacts contribute coordinate samples to capture analysis.</summary>
public static class AnalysisContactRules
{
    /// <summary>Returns whether a contact is not invalid and is entering, moving, or a palm with NoFinger status.</summary>
    public static bool IsCoordinateSample(AnalysisContact contact) =>
        !contact.Invalid &&
        (contact.Status is TouchStatus.Enter or TouchStatus.Move ||
         contact.Type == TouchType.Palm && contact.Status == TouchStatus.NoFinger);
}

/// <summary>Holds a selected replay frame's source references, timing, host state eligibility, and reported contacts.</summary>
/// <param name="Id">The event identifier derived from the logical index and primary source record identifier.</param>
/// <param name="LogicalIndex">The logical frame index within the replay.</param>
/// <param name="SourceRecordId">The stable identifier of the frame's primary source record.</param>
/// <param name="PhysicalRecordIds">The distinct stable identifiers of the frame's physical source records.</param>
/// <param name="CapturedTime">The recorded time of the replay frame.</param>
/// <param name="FrameTime">The replay frame's position on the frame timeline.</param>
/// <param name="TimestampSynthetic">Whether the frame's timestamp was synthesized.</param>
/// <param name="HostStateEligible">Whether the replay snapshot updated host state.</param>
/// <param name="GlobalPalm">The global palm flag reported by the replay snapshot.</param>
/// <param name="ReportedContacts">The contacts reported in the replay snapshot.</param>
public sealed record AnalysisEvent(
    string Id,
    int LogicalIndex,
    string SourceRecordId,
    IReadOnlyList<string> PhysicalRecordIds,
    TimeSpan CapturedTime,
    TimeSpan FrameTime,
    bool TimestampSynthetic,
    bool HostStateEligible,
    bool GlobalPalm,
    IReadOnlyList<AnalysisContact> ReportedContacts);

/// <summary>Holds a replay diagnostic's severity, message, source location, associated analysis events, and details.</summary>
/// <param name="Id">The diagnostic identifier derived from its selected-list index and diagnostic code.</param>
/// <param name="Severity">The severity of the replay diagnostic.</param>
/// <param name="Code">The code of the replay diagnostic.</param>
/// <param name="Message">The message of the replay diagnostic.</param>
/// <param name="SourceRecordId">The source record identifier referenced by the diagnostic.</param>
/// <param name="Location">The diagnostic's location in the source capture.</param>
/// <param name="EventIds">The distinct analysis event identifiers associated with the diagnostic's source record.</param>
/// <param name="Details">The diagnostic's key-value details, or an empty dictionary when none were supplied.</param>
public sealed record AnalysisDiagnostic(
    string Id,
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string SourceRecordId,
    SourceLocation Location,
    IReadOnlyList<string> EventIds,
    IReadOnlyDictionary<string, string> Details);

/// <summary>Groups selected diagnostics by code with maximum severity, source references, and review state.</summary>
/// <param name="Code">The diagnostic code shared by the group.</param>
/// <param name="MaximumSeverity">The highest severity among the grouped diagnostics.</param>
/// <param name="Count">The number of diagnostics in the group.</param>
/// <param name="DiagnosticIds">The identifiers of the grouped analysis diagnostics.</param>
/// <param name="EventIds">The distinct analysis event identifiers referenced by the group.</param>
/// <param name="SourceRecordIds">The distinct source record identifiers referenced by the group.</param>
/// <param name="WorkflowState">The group's review workflow state, or Open when no review group exists.</param>
/// <param name="Disposition">The group's review disposition, or None when no review group exists.</param>
public sealed record AnalysisDiagnosticAggregate(
    string Code,
    DiagnosticSeverity MaximumSeverity,
    int Count,
    IReadOnlyList<string> DiagnosticIds,
    IReadOnlyList<string> EventIds,
    IReadOnlyList<string> SourceRecordIds,
    ReviewWorkflowState WorkflowState,
    ReviewDisposition Disposition);

/// <summary>Holds an ASIL alarm interval's transition references, durations, and assertion state.</summary>
/// <param name="AssertionDiagnosticId">The identifier of the diagnostic that asserted the ASIL alarm.</param>
/// <param name="ClearDiagnosticId">The clearing diagnostic identifier, or null if the interval remains asserted.</param>
/// <param name="AssertionEventId">The identifier of the event associated with the assertion, when available.</param>
/// <param name="ClearEventId">The clearing event identifier, or null if the interval remains asserted.</param>
/// <param name="CapturedDuration">The captured time from assertion to clear or to the last selected event.</param>
/// <param name="FrameDuration">The frame time from assertion to clear or to the last selected event.</param>
/// <param name="RemainsAsserted">Whether the alarm remains asserted at the last selected event.</param>
public sealed record AnalysisAsilPeriod(
    string AssertionDiagnosticId,
    string? ClearDiagnosticId,
    string? AssertionEventId,
    string? ClearEventId,
    TimeSpan CapturedDuration,
    TimeSpan FrameDuration,
    bool RemainsAsserted);

/// <summary>Summarizes ASIL alarm transitions, review group states, and alarm intervals.</summary>
/// <param name="Assertions">The number of ASIL alarm diagnostics linked to selected events.</param>
/// <param name="Clears">The number of ASIL clear diagnostics linked to selected events.</param>
/// <param name="AcknowledgedGroups">The number of ASIL review groups whose workflow state is not Open.</param>
/// <param name="UnresolvedAlarmGroups">The number of ASIL review groups with Alarm severity whose workflow state is not Resolved.</param>
/// <param name="Periods">The alarm intervals from assertion to clear or to the last selected event.</param>
public sealed record AnalysisAsilSummary(
    int Assertions,
    int Clears,
    int AcknowledgedGroups,
    int UnresolvedAlarmGroups,
    IReadOnlyList<AnalysisAsilPeriod> Periods);

/// <summary>Describes the coordinate mapping kind and axis bounds used for the analysis heatmap.</summary>
/// <param name="Kind">The identifier of the coordinate mapping, such as linear-origin-top-left.</param>
/// <param name="MinimumX">The lower X bound of the coordinate mapping.</param>
/// <param name="MaximumX">The upper X bound used to scale contact coordinates into heatmap columns.</param>
/// <param name="MinimumY">The lower Y bound of the coordinate mapping.</param>
/// <param name="MaximumY">The upper Y bound used to scale contact coordinates into heatmap rows.</param>
public sealed record AnalysisCoordinateTransform(
    string Kind,
    double MinimumX,
    double MaximumX,
    double MinimumY,
    double MaximumY);

/// <summary>Holds heatmap grid dimensions, coordinate sample counts, and the coordinate transform.</summary>
/// <param name="Columns">The number of columns in the heatmap grid.</param>
/// <param name="Rows">The number of rows in the heatmap grid.</param>
/// <param name="Counts">The coordinate sample counts indexed by row times Columns plus column.</param>
/// <param name="SampleCount">The total number of contacts accepted as coordinate samples.</param>
/// <param name="Transform">The coordinate mapping used to assign samples to heatmap cells.</param>
public sealed record AnalysisHotspot(
    int Columns,
    int Rows,
    IReadOnlyList<int> Counts,
    int SampleCount,
    AnalysisCoordinateTransform Transform);

/// <summary>Records the analysis schema, source identity, decode settings, selected range, timing, and heatmap metadata.</summary>
/// <param name="SchemaVersion">The schema version of the analysis report.</param>
/// <param name="SourceFileName">The file name of the source capture without its directory.</param>
/// <param name="SourceSha256">The SHA-256 hash identifying the source capture.</param>
/// <param name="DecodeConfiguration">The configuration supplied for decoding the capture.</param>
/// <param name="FormatEvidenceStatus">The supplied evidence status of the capture format.</param>
/// <param name="Range">The inclusive logical frame range selected for analysis.</param>
/// <param name="Clock">The timing summary for the selected range.</param>
/// <param name="HeatmapPixelWidth">The output heatmap width in pixels.</param>
/// <param name="HeatmapPixelHeight">The output heatmap height in pixels.</param>
/// <param name="HeatmapTransform">The coordinate mapping used by the output heatmap.</param>
public sealed record AnalysisManifest(
    int SchemaVersion,
    string SourceFileName,
    string SourceSha256,
    ReplayDecodeConfiguration DecodeConfiguration,
    EvidenceStatus FormatEvidenceStatus,
    AnalysisRange Range,
    AnalysisClockSummary Clock,
    int HeatmapPixelWidth,
    int HeatmapPixelHeight,
    AnalysisCoordinateTransform HeatmapTransform);

/// <summary>Combines analysis provenance, selected events, diagnostics, ASIL alarm summaries, and heatmap data.</summary>
/// <param name="Manifest">The source identity, configuration, range, timing, and heatmap metadata.</param>
/// <param name="Events">The replay events within the selected logical frame range.</param>
/// <param name="Diagnostics">The diagnostics retained for the analysis.</param>
/// <param name="DiagnosticAggregates">The retained diagnostics grouped by code with review states.</param>
/// <param name="Asil">The ASIL alarm transition and review summary.</param>
/// <param name="Hotspot">The grid of accepted coordinate sample counts.</param>
public sealed record CaptureAnalysisReport(
    AnalysisManifest Manifest,
    IReadOnlyList<AnalysisEvent> Events,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics,
    IReadOnlyList<AnalysisDiagnosticAggregate> DiagnosticAggregates,
    AnalysisAsilSummary Asil,
    AnalysisHotspot Hotspot);

/// <summary>Produces capture analysis reports from replay snapshots, diagnostics, and optional review state.</summary>
public sealed class CaptureAnalyzer
{
    /// <summary>Analyzes a logical frame range to produce source-linked events, diagnostics, ASIL intervals, and a heatmap.</summary>
    public CaptureAnalysisReport Analyze(
        string sourcePath,
        string sourceSha256,
        ReplayDecodeConfiguration configuration,
        EvidenceStatus formatEvidenceStatus,
        ITouchReplaySession replay,
        IEnumerable<ReplayDiagnostic> diagnostics,
        ReviewSession? review = null,
        AnalysisRange? range = null,
        int heatmapColumns = 64,
        int heatmapRows = 36,
        int heatmapPixelWidth = 640,
        int heatmapPixelHeight = 360,
        CancellationToken cancellationToken = default,
        IReadOnlyList<ITouchReplaySnapshot>? snapshots = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (replay.Count == 0) throw new InvalidOperationException("Analysis requires at least one decoded frame.");
        if (snapshots is not null && snapshots.Count != replay.Count)
            throw new ArgumentException("Cached snapshot count must match the replay session.", nameof(snapshots));
        if (heatmapColumns <= 0 || heatmapRows <= 0 || heatmapPixelWidth <= 0 || heatmapPixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(heatmapColumns), "Heatmap dimensions must be positive.");

        var selected = range ?? new AnalysisRange(0, replay.Count - 1);
        if (selected.StartLogicalIndex < 0 || selected.EndLogicalIndex < selected.StartLogicalIndex || selected.EndLogicalIndex >= replay.Count)
            throw new ArgumentOutOfRangeException(nameof(range), "Analysis range must be within the decoded replay.");

        var events = new List<AnalysisEvent>(selected.EndLogicalIndex - selected.StartLogicalIndex + 1);
        var eventIdsBySource = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var allReplaySourceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < replay.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = snapshots?[index] ?? replay.Seek(index);
            foreach (var sourceId in snapshot.PhysicalRecords.Append(snapshot.PrimarySource).Select(item => item.StableId))
                allReplaySourceIds.Add(sourceId);
            if (index < selected.StartLogicalIndex || index > selected.EndLogicalIndex) continue;
            var eventId = $"event-{index:D8}-{snapshot.PrimarySource.StableId}";
            var physicalIds = snapshot.PhysicalRecords.Select(item => item.StableId).Distinct(StringComparer.Ordinal).ToArray();
            events.Add(new AnalysisEvent(
                eventId,
                index,
                snapshot.PrimarySource.StableId,
                physicalIds,
                snapshot.Timeline.RecordedTime,
                snapshot.Timeline.FrameTime,
                snapshot.Timeline.TimestampSynthetic,
                snapshot.HostStateUpdated,
                snapshot.GlobalPalm,
                snapshot.ReportedContacts.Select(contact => new AnalysisContact(
                    contact.Id, contact.Type, contact.Status, contact.X, contact.Y, contact.Invalid)).ToArray()));
            foreach (var sourceId in physicalIds.Append(snapshot.PrimarySource.StableId).Distinct(StringComparer.Ordinal))
            {
                if (!eventIdsBySource.TryGetValue(sourceId, out var ids)) eventIdsBySource[sourceId] = ids = [];
                ids.Add(eventId);
            }
        }

        var selectedDiagnosticList = new List<AnalysisDiagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!eventIdsBySource.ContainsKey(diagnostic.SourceRecordId) &&
                allReplaySourceIds.Contains(diagnostic.SourceRecordId))
                continue;
            var diagnosticIndex = selectedDiagnosticList.Count;
            selectedDiagnosticList.Add(new AnalysisDiagnostic(
                $"diagnostic-{diagnosticIndex:D8}-{diagnostic.Code}",
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.SourceRecordId,
                diagnostic.Location,
                eventIdsBySource.GetValueOrDefault(diagnostic.SourceRecordId, []).Distinct(StringComparer.Ordinal).ToArray(),
                diagnostic.Details ?? new Dictionary<string, string>()));
        }
        var selectedDiagnostics = selectedDiagnosticList.ToArray();

        var reviewByCode = new Dictionary<string, ReviewEventGroup>(StringComparer.Ordinal);
        foreach (var group in review?.Groups ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            reviewByCode.TryAdd(group.Code, group);
        }
        var diagnosticsByCode = new Dictionary<string, List<AnalysisDiagnostic>>(StringComparer.Ordinal);
        foreach (var diagnostic in selectedDiagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!diagnosticsByCode.TryGetValue(diagnostic.Code, out var group))
                diagnosticsByCode[diagnostic.Code] = group = [];
            group.Add(diagnostic);
        }
        var aggregateList = new List<AnalysisDiagnosticAggregate>(diagnosticsByCode.Count);
        foreach (var code in diagnosticsByCode.Keys.Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = diagnosticsByCode[code];
            var state = reviewByCode.GetValueOrDefault(code);
            var severity = group[0].Severity;
            var diagnosticIds = new string[group.Count];
            var eventIds = new List<string>();
            var uniqueEventIds = new HashSet<string>(StringComparer.Ordinal);
            var sourceIds = new List<string>();
            var uniqueSourceIds = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < group.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diagnostic = group[index];
                if (diagnostic.Severity > severity) severity = diagnostic.Severity;
                diagnosticIds[index] = diagnostic.Id;
                if (uniqueSourceIds.Add(diagnostic.SourceRecordId)) sourceIds.Add(diagnostic.SourceRecordId);
                foreach (var eventId in diagnostic.EventIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (uniqueEventIds.Add(eventId)) eventIds.Add(eventId);
                }
            }
            aggregateList.Add(new AnalysisDiagnosticAggregate(
                code,
                severity,
                group.Count,
                diagnosticIds,
                eventIds,
                sourceIds,
                state?.WorkflowState ?? ReviewWorkflowState.Open,
                state?.Disposition ?? ReviewDisposition.None));
        }
        var aggregates = aggregateList.ToArray();

        var timeline = replay.Timeline;
        var startEntry = timeline[selected.StartLogicalIndex];
        var endEntry = timeline[selected.EndLogicalIndex];
        var syntheticTimestampFrames = 0;
        var compressedIdleGaps = 0;
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.TimestampSynthetic) syntheticTimestampFrames++;
        }
        for (var index = selected.StartLogicalIndex; index <= selected.EndLogicalIndex; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeline[index].IdleGapCompressed) compressedIdleGaps++;
        }
        var clock = new AnalysisClockSummary(
            syntheticTimestampFrames == 0 ? "captured" : syntheticTimestampFrames == events.Count ? "synthetic-frame" : "mixed",
            endEntry.RecordedTime - startEntry.RecordedTime,
            endEntry.FrameTime - startEntry.FrameTime,
            syntheticTimestampFrames,
            compressedIdleGaps);

        var validSampleList = new List<AnalysisContact>();
        var maximumSampleX = 0d;
        var maximumSampleY = 0d;
        foreach (var analysisEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var contact in analysisEvent.ReportedContacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AnalysisContactRules.IsCoordinateSample(contact))
                {
                    validSampleList.Add(contact);
                    maximumSampleX = Math.Max(maximumSampleX, contact.X);
                    maximumSampleY = Math.Max(maximumSampleY, contact.Y);
                }
            }
        }
        var validSamples = validSampleList.ToArray();
        var transform = new AnalysisCoordinateTransform(
            "linear-origin-top-left",
            0,
            NiceExtent(maximumSampleX, 1920),
            0,
            NiceExtent(maximumSampleY, 1080));
        var counts = new int[heatmapColumns * heatmapRows];
        foreach (var sample in validSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var column = Math.Clamp((int)(sample.X / transform.MaximumX * heatmapColumns), 0, heatmapColumns - 1);
            var row = Math.Clamp((int)(sample.Y / transform.MaximumY * heatmapRows), 0, heatmapRows - 1);
            counts[(row * heatmapColumns) + column]++;
        }
        var hotspot = new AnalysisHotspot(heatmapColumns, heatmapRows, counts, validSamples.Length, transform);
        var asil = BuildAsilSummary(selectedDiagnostics, events, review, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = new AnalysisManifest(
            1,
            Path.GetFileName(sourcePath),
            sourceSha256,
            configuration,
            formatEvidenceStatus,
            selected,
            clock,
            heatmapPixelWidth,
            heatmapPixelHeight,
            transform);
        return new CaptureAnalysisReport(manifest, events, selectedDiagnostics, aggregates, asil, hotspot);
    }

    private static AnalysisAsilSummary BuildAsilSummary(
        IReadOnlyList<AnalysisDiagnostic> diagnostics,
        IReadOnlyList<AnalysisEvent> events,
        ReviewSession? review,
        CancellationToken cancellationToken)
    {
        var eventsById = new Dictionary<string, AnalysisEvent>(events.Count, StringComparer.Ordinal);
        foreach (var analysisEvent in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventsById[analysisEvent.Id] = analysisEvent;
        }
        var transitionList = new List<(AnalysisDiagnostic Diagnostic, AnalysisEvent? Event)>();
        foreach (var diagnostic in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (diagnostic.Code is not ("COMMON_ASIL_ALARM" or "COMMON_ASIL_CLEARED")) continue;
            AnalysisEvent? analysisEvent = null;
            foreach (var eventId in diagnostic.EventIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (eventsById.TryGetValue(eventId, out var candidate) &&
                    (analysisEvent is null || candidate.LogicalIndex < analysisEvent.LogicalIndex))
                    analysisEvent = candidate;
            }
            if (analysisEvent is not null) transitionList.Add((diagnostic, analysisEvent));
        }
        var transitions = transitionList.OrderBy(item => item.Event!.LogicalIndex).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var periods = new List<AnalysisAsilPeriod>();
        (AnalysisDiagnostic Diagnostic, AnalysisEvent? Event)? assertion = null;
        var assertionCount = 0;
        var clearCount = 0;
        foreach (var transition in transitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (transition.Diagnostic.Code == "COMMON_ASIL_ALARM")
            {
                assertionCount++;
                assertion ??= transition;
            }
            else if (assertion is { } active)
            {
                clearCount++;
                periods.Add(Period(active, transition, remainsAsserted: false));
                assertion = null;
            }
            else
            {
                clearCount++;
            }
        }
        if (assertion is { } remaining)
        {
            var last = events[^1];
            periods.Add(new AnalysisAsilPeriod(
                remaining.Diagnostic.Id,
                null,
                remaining.Event?.Id,
                null,
                last.CapturedTime - (remaining.Event?.CapturedTime ?? last.CapturedTime),
                last.FrameTime - (remaining.Event?.FrameTime ?? last.FrameTime),
                true));
        }
        var asilGroups = new List<ReviewEventGroup>();
        var acknowledgedGroups = 0;
        var unresolvedAlarms = 0;
        foreach (var group in review?.Groups ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!group.IsAsil) continue;
            asilGroups.Add(group);
            if (group.WorkflowState != ReviewWorkflowState.Open) acknowledgedGroups++;
            if (group.Severity == DiagnosticSeverity.Alarm && group.WorkflowState != ReviewWorkflowState.Resolved)
                unresolvedAlarms++;
        }
        return new AnalysisAsilSummary(
            assertionCount,
            clearCount,
            acknowledgedGroups,
            unresolvedAlarms,
            periods);

        static AnalysisAsilPeriod Period(
            (AnalysisDiagnostic Diagnostic, AnalysisEvent? Event) start,
            (AnalysisDiagnostic Diagnostic, AnalysisEvent? Event) end,
            bool remainsAsserted) => new(
                start.Diagnostic.Id,
                end.Diagnostic.Id,
                start.Event?.Id,
                end.Event?.Id,
                (end.Event?.CapturedTime ?? TimeSpan.Zero) - (start.Event?.CapturedTime ?? TimeSpan.Zero),
                (end.Event?.FrameTime ?? TimeSpan.Zero) - (start.Event?.FrameTime ?? TimeSpan.Zero),
                remainsAsserted);
    }

    private static double NiceExtent(double maximum, double minimum)
    {
        var required = Math.Max(minimum, maximum * 1.08);
        return Math.Ceiling(required / 256) * 256;
    }
}
