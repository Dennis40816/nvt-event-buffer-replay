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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSha256);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(replay);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (replay.Count == 0) throw new InvalidOperationException("Analysis requires at least one decoded frame.");
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
            var snapshot = replay.Seek(index);
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

        var selectedDiagnostics = diagnostics
            .Where(item => eventIdsBySource.ContainsKey(item.SourceRecordId) || !allReplaySourceIds.Contains(item.SourceRecordId))
            .Select((diagnostic, index) => new AnalysisDiagnostic(
                $"diagnostic-{index:D8}-{diagnostic.Code}",
                diagnostic.Severity,
                diagnostic.Code,
                diagnostic.Message,
                diagnostic.SourceRecordId,
                diagnostic.Location,
                eventIdsBySource.GetValueOrDefault(diagnostic.SourceRecordId, []).Distinct(StringComparer.Ordinal).ToArray(),
                diagnostic.Details ?? new Dictionary<string, string>()))
            .ToArray();

        var reviewByCode = (review?.Groups ?? [])
            .GroupBy(group => group.Code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var aggregates = selectedDiagnostics
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var state = reviewByCode.GetValueOrDefault(group.Key);
                return new AnalysisDiagnosticAggregate(
                    group.Key,
                    group.Max(item => item.Severity),
                    group.Count(),
                    group.Select(item => item.Id).ToArray(),
                    group.SelectMany(item => item.EventIds).Distinct(StringComparer.Ordinal).ToArray(),
                    group.Select(item => item.SourceRecordId).Distinct(StringComparer.Ordinal).ToArray(),
                    state?.WorkflowState ?? ReviewWorkflowState.Open,
                    state?.Disposition ?? ReviewDisposition.None);
            })
            .ToArray();

        var timeline = replay.Timeline;
        var startEntry = timeline[selected.StartLogicalIndex];
        var endEntry = timeline[selected.EndLogicalIndex];
        var clock = new AnalysisClockSummary(
            events.All(item => !item.TimestampSynthetic) ? "captured" : events.All(item => item.TimestampSynthetic) ? "synthetic-frame" : "mixed",
            endEntry.RecordedTime - startEntry.RecordedTime,
            endEntry.FrameTime - startEntry.FrameTime,
            events.Count(item => item.TimestampSynthetic),
            timeline.Skip(selected.StartLogicalIndex).Take(events.Count).Count(item => item.IdleGapCompressed));

        var validSamples = events.SelectMany(item => item.ReportedContacts)
            .Where(contact => !contact.Invalid && (contact.Status is TouchStatus.Enter or TouchStatus.Move || contact.Type == TouchType.Palm && contact.Status == TouchStatus.NoFinger))
            .ToArray();
        var transform = new AnalysisCoordinateTransform(
            "linear-origin-top-left",
            0,
            NiceExtent(validSamples.Select(item => (double)item.X).DefaultIfEmpty(1920).Max(), 1920),
            0,
            NiceExtent(validSamples.Select(item => (double)item.Y).DefaultIfEmpty(1080).Max(), 1080));
        var counts = new int[heatmapColumns * heatmapRows];
        foreach (var sample in validSamples)
        {
            var column = Math.Clamp((int)(sample.X / transform.MaximumX * heatmapColumns), 0, heatmapColumns - 1);
            var row = Math.Clamp((int)(sample.Y / transform.MaximumY * heatmapRows), 0, heatmapRows - 1);
            counts[(row * heatmapColumns) + column]++;
        }
        var hotspot = new AnalysisHotspot(heatmapColumns, heatmapRows, counts, validSamples.Length, transform);
        var asil = BuildAsilSummary(selectedDiagnostics, events, review);
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
        ReviewSession? review)
    {
        var transitions = diagnostics
            .Where(item => item.Code is "COMMON_ASIL_ALARM" or "COMMON_ASIL_CLEARED")
            .Select(item => (Diagnostic: item, Event: events.FirstOrDefault(candidate => item.EventIds.Contains(candidate.Id))))
            .Where(item => item.Event is not null)
            .OrderBy(item => item.Event!.LogicalIndex)
            .ToArray();
        var periods = new List<AnalysisAsilPeriod>();
        (AnalysisDiagnostic Diagnostic, AnalysisEvent? Event)? assertion = null;
        foreach (var transition in transitions)
        {
            if (transition.Diagnostic.Code == "COMMON_ASIL_ALARM")
            {
                assertion ??= transition;
            }
            else if (assertion is { } active)
            {
                periods.Add(Period(active, transition, remainsAsserted: false));
                assertion = null;
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
        var asilGroups = review?.Groups.Where(group => group.IsAsil).ToArray() ?? [];
        return new AnalysisAsilSummary(
            transitions.Count(item => item.Diagnostic.Code == "COMMON_ASIL_ALARM"),
            transitions.Count(item => item.Diagnostic.Code == "COMMON_ASIL_CLEARED"),
            asilGroups.Count(group => group.WorkflowState != ReviewWorkflowState.Open),
            asilGroups.Count(group => group.Severity == DiagnosticSeverity.Alarm && group.WorkflowState != ReviewWorkflowState.Resolved),
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
