namespace Nvt.Replay.Core;

/// <summary>Classifies replay diagnostics as informational messages, warnings, alarms or errors.</summary>
public enum DiagnosticSeverity
{
    /// <summary>Classifies a replay diagnostic as an informational message.</summary>
    Info,
    /// <summary>Classifies a replay diagnostic as a warning.</summary>
    Warning,
    /// <summary>Classifies a replay diagnostic as an alarm.</summary>
    Alarm,
    /// <summary>Classifies a replay diagnostic as an error.</summary>
    Error,
}

/// <summary>Holds a diagnostic's severity, code, message, source record reference and optional details.</summary>
/// <param name="Severity">The severity assigned to the diagnostic.</param>
/// <param name="Code">The code identifying the diagnostic condition.</param>
/// <param name="Message">The message describing the diagnostic condition.</param>
/// <param name="SourceRecordId">The identifier linking the diagnostic to a source record.</param>
/// <param name="Location">The source byte offset and line or worksheet row associated with the diagnostic.</param>
/// <param name="Details">Optional key-value data describing the diagnostic condition.</param>
public sealed record ReplayDiagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message,
    string SourceRecordId,
    SourceLocation Location,
    IReadOnlyDictionary<string, string>? Details = null);
