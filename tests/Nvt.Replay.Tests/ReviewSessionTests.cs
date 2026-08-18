using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class ReviewSessionTests
{
    [Fact]
    public void Repeated_events_group_without_losing_raw_occurrences()
    {
        var first = Diagnostic(DiagnosticSeverity.Warning, "COMMON_BUS_COUNTER_INCREASED", "a", 1, ("counter", "busy"));
        var second = Diagnostic(DiagnosticSeverity.Warning, "COMMON_BUS_COUNTER_INCREASED", "b", 2, ("counter", "busy"));
        var other = Diagnostic(DiagnosticSeverity.Warning, "COMMON_BUS_COUNTER_INCREASED", "c", 3, ("counter", "no_response"));
        var session = Session([first, second, other]);

        var groups = session.Groups;

        Assert.Equal(2, groups.Count);
        var busy = Assert.Single(groups, group => group.CorrelationKey.EndsWith(":busy", StringComparison.Ordinal));
        Assert.Equal([first, second], busy.Occurrences.Select(item => item.Diagnostic));
        Assert.Equal([0, 1], busy.Occurrences.Select(item => item.LogicalIndex));
    }

    [Fact]
    public void Asil_captured_lifecycle_and_human_state_are_independent()
    {
        var alarm = Diagnostic(DiagnosticSeverity.Alarm, "COMMON_ASIL_ALARM", "assert", 1, ("asil_byte_raw", "0x1A"));
        var clear = Diagnostic(DiagnosticSeverity.Info, "COMMON_ASIL_CLEARED", "clear", 2);
        var source = new[] { alarm, clear };
        var session = Session(source);
        var group = Assert.Single(session.Groups);

        Assert.Equal(CapturedAlarmState.Cleared, group.CurrentCapturedAlarmState);
        Assert.Equal(AsilLifecycleState.Cleared, group.AsilLifecycle);

        group = session.Acknowledge(group.Id);
        Assert.Equal(ReviewWorkflowState.Acknowledged, group.WorkflowState);
        Assert.Equal(AsilLifecycleState.Cleared, group.AsilLifecycle);

        group = session.SetDisposition(group.Id, ReviewDisposition.Expected);
        group = session.Resolve(group.Id);
        Assert.Equal(ReviewWorkflowState.Resolved, group.WorkflowState);
        Assert.Equal(ReviewDisposition.Expected, group.Disposition);
        Assert.Equal(AsilLifecycleState.Resolved, group.AsilLifecycle);
        Assert.Same(source, session.Diagnostics);
        Assert.Equal("0x1A", alarm.Details?["asil_byte_raw"]);
    }

    [Fact]
    public void Asserted_Asil_cannot_be_resolved_and_defaults_to_alarm_pause()
    {
        var alarm = Diagnostic(DiagnosticSeverity.Alarm, "COMMON_ASIL_ALARM", "assert", 2, ("asil_byte_raw", "0x07"));
        var session = Session([alarm]);
        var acknowledged = session.Acknowledge(Assert.Single(session.Groups).Id);

        Assert.Equal(AsilLifecycleState.Acknowledged, acknowledged.AsilLifecycle);
        Assert.Throws<InvalidOperationException>(() => session.Resolve(acknowledged.Id));
        Assert.True(session.ShouldPauseAt(0));

        session.Options = session.Options with { PauseOnAlarm = false };
        Assert.False(session.ShouldPauseAt(0));
    }

    [Fact]
    public void Qa_fail_pauses_and_filters_and_order_are_deterministic()
    {
        var warning = Diagnostic(DiagnosticSeverity.Warning, "COMMON_CRC_MISMATCH", "crc", 1);
        var info = Diagnostic(DiagnosticSeverity.Info, "QA_CASE_PASS", "pass", 2, ("result", "pass"));
        var fail = Diagnostic(DiagnosticSeverity.Error, "QA_CASE_FAIL", "fail", 3, ("result", "fail"));
        var alarm = Diagnostic(DiagnosticSeverity.Alarm, "DESAY97_TP_ASIL_ALARM", "asil", 4);
        var session = Session([warning, info, fail, alarm]);

        Assert.Equal(
            [DiagnosticSeverity.Alarm, DiagnosticSeverity.Error, DiagnosticSeverity.Warning, DiagnosticSeverity.Info],
            session.Groups.Select(group => group.Severity));
        Assert.Single(session.Filter(ReviewQueueFilter.Asil));
        Assert.Equal(2, session.Filter(ReviewQueueFilter.Warnings).Count);
        Assert.True(session.ShouldPauseAt(2));
        Assert.Equal(2, session.FirstPauseIndex(0, 3));
    }

    [Fact]
    public void Pause_index_lookup_honors_runtime_options_and_range_boundaries()
    {
        var warning = Diagnostic(DiagnosticSeverity.Warning, "COMMON_CRC_MISMATCH", "crc", 1);
        var firstFail = Diagnostic(DiagnosticSeverity.Error, "QA_CASE_FAIL", "fail", 2, ("result", "fail"));
        var alarm = Diagnostic(DiagnosticSeverity.Alarm, "COMMON_ASIL_ALARM", "asil", 3);
        var secondFail = Diagnostic(DiagnosticSeverity.Error, "QA_CASE_FAIL", "fail", 4, ("result", "fail"));
        var session = Session([warning, firstFail, alarm, secondFail]);

        Assert.Equal(1, session.FirstPauseIndex(0, 3));

        session.Options = new ReviewSessionOptions(PauseOnAlarm: true, PauseOnQaFail: false);
        Assert.Equal(2, session.FirstPauseIndex(0, 3));
        Assert.True(session.ShouldPauseAt(2));
        Assert.False(session.ShouldPauseAt(1));

        session.Options = new ReviewSessionOptions(PauseOnAlarm: false, PauseOnQaFail: true);
        Assert.Equal(3, session.FirstPauseIndex(1, 3));

        session.Options = new ReviewSessionOptions(PauseOnAlarm: false, PauseOnQaFail: false);
        Assert.Null(session.FirstPauseIndex(-1, 3));
    }

    [Fact]
    public void Frame_alert_lookup_is_indexed_by_source_or_line_and_deduplicates_matches()
    {
        var sourceAlert = Diagnostic(DiagnosticSeverity.Warning, "SOURCE_WARNING", "source", 1);
        var lineAlert = Diagnostic(DiagnosticSeverity.Alarm, "LINE_ALARM", "line", 2);
        var info = Diagnostic(DiagnosticSeverity.Info, "INFO", "ignored", 3);
        var session = Session([sourceAlert, lineAlert, info]);

        Assert.True(session.HasNavigableFindings);
        Assert.Equal(
            [sourceAlert, lineAlert],
            session.FrameAlerts(sourceAlert.SourceRecordId, lineAlert.Location.LineNumber));
        Assert.Equal([sourceAlert], session.FrameAlerts(sourceAlert.SourceRecordId, sourceAlert.Location.LineNumber));
        Assert.Empty(session.FrameAlerts(info.SourceRecordId, info.Location.LineNumber));
    }

    [Fact]
    public void Reserved_Asil_error_code_remains_raw_evidence()
    {
        var diagnostic = Diagnostic(DiagnosticSeverity.Alarm, "COMMON_ASIL_ALARM", "reserved", 1,
            ("asil_byte_raw", "0x07"), ("error_code", "7"));

        var occurrence = Assert.Single(Assert.Single(Session([diagnostic]).Groups).Occurrences);

        Assert.Equal("7", occurrence.Diagnostic.Details?["error_code"]);
        Assert.Equal("0x07", occurrence.Diagnostic.Details?["asil_byte_raw"]);
    }

    private static ReviewSession Session(IReadOnlyList<ReplayDiagnostic> diagnostics) =>
        new(diagnostics, diagnostics.Select((diagnostic, index) => (diagnostic.SourceRecordId, index))
            .ToDictionary(item => item.SourceRecordId, item => item.index, StringComparer.Ordinal));

    private static ReplayDiagnostic Diagnostic(
        DiagnosticSeverity severity,
        string code,
        string message,
        int source,
        params (string Key, string Value)[] details) =>
        new(
            severity,
            code,
            message,
            $"source-{source}",
            new SourceLocation(source * 100, source),
            details.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
}
