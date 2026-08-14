namespace Nvt.Replay.Core;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Alarm,
    Error,
}

public sealed record ReplayDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string SourceRecordId,
    SourceLocation Location,
    IReadOnlyDictionary<string, string>? Details = null);
