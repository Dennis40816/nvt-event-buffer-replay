using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

public sealed record AnalysisRange(int StartLogicalIndex, int EndLogicalIndex);

public sealed record AnalysisClockSummary(
    string Domain,
    TimeSpan CapturedDuration,
    TimeSpan FrameDuration,
    int SyntheticTimestampFrames,
    int CompressedIdleGaps);

public sealed record AnalysisContact(
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    bool Invalid);

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

public sealed record AnalysisDiagnostic(
    string Id,
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string SourceRecordId,
    SourceLocation Location,
    IReadOnlyList<string> EventIds,
    IReadOnlyDictionary<string, string> Details);

public sealed record AnalysisDiagnosticAggregate(
    string Code,
    DiagnosticSeverity MaximumSeverity,
    int Count,
    IReadOnlyList<string> DiagnosticIds,
    IReadOnlyList<string> EventIds,
    IReadOnlyList<string> SourceRecordIds,
    ReviewWorkflowState WorkflowState,
    ReviewDisposition Disposition);

public sealed record AnalysisAsilPeriod(
    string AssertionDiagnosticId,
    string? ClearDiagnosticId,
    string? AssertionEventId,
    string? ClearEventId,
    TimeSpan CapturedDuration,
    TimeSpan FrameDuration,
    bool RemainsAsserted);

public sealed record AnalysisAsilSummary(
    int Assertions,
    int Clears,
    int AcknowledgedGroups,
    int UnresolvedAlarmGroups,
    IReadOnlyList<AnalysisAsilPeriod> Periods);

public sealed record AnalysisCoordinateTransform(
    string Kind,
    double MinimumX,
    double MaximumX,
    double MinimumY,
    double MaximumY);

public sealed record AnalysisHotspot(
    int Columns,
    int Rows,
    IReadOnlyList<int> Counts,
    int SampleCount,
    AnalysisCoordinateTransform Transform);

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

public sealed record CaptureAnalysisReport(
    AnalysisManifest Manifest,
    IReadOnlyList<AnalysisEvent> Events,
    IReadOnlyList<AnalysisDiagnostic> Diagnostics,
    IReadOnlyList<AnalysisDiagnosticAggregate> DiagnosticAggregates,
    AnalysisAsilSummary Asil,
    AnalysisHotspot Hotspot);

public sealed class CaptureAnalyzer
{
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
                if (!contact.Invalid &&
                    (contact.Status is TouchStatus.Enter or TouchStatus.Move ||
                     contact.Type == TouchType.Palm && contact.Status == TouchStatus.NoFinger))
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
