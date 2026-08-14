using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

internal static class SourceDiagnostics
{
    public static void Report(
        SourceOpenContext context,
        DiagnosticSeverity severity,
        string code,
        string message,
        int lineNumber,
        long byteOffset = 0,
        IReadOnlyDictionary<string, string>? details = null) =>
        context.DiagnosticSink?.Invoke(new ReplayDiagnostic(
            severity,
            code,
            message,
            $"{context.SourceId}:L{lineNumber}",
            new SourceLocation(byteOffset, lineNumber),
            details));
}
