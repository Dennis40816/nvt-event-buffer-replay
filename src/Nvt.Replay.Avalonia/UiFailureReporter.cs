// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Diagnostics;
using Nvt.Core.Threading;

namespace Nvt.Replay.Avalonia;

internal sealed record UiFailureReporterSeams(
    TimeProvider TimeProvider,
    Action<string, string> ResetBusyAndSetStatus,
    Action<string, Exception> WriteDiagnostics,
    Action? ResetPreviewPlayback = null,
    Action? RefreshCaptureSetup = null);

internal sealed class UiFailureReporter
{
    private readonly UiFailureReporterSeams _seams;

    internal UiFailureReporter(UiFailureReporterSeams seams)
    {
        _seams = seams;
        Runner = new UiEventRunner(Report, ReportFallback);
    }

    internal UiEventRunner Runner { get; }
    internal TimeProvider TimeProvider => _seams.TimeProvider;

    internal void Report(string operation, Exception exception)
    {
        _seams.ResetBusyAndSetStatus(operation, $"{FailureLabel(operation)} · {exception.Message}");
        if (string.Equals(operation, "output.preview-play-pause", StringComparison.Ordinal))
            _seams.ResetPreviewPlayback?.Invoke();
        if (string.Equals(operation, "capture.apply-setup", StringComparison.Ordinal))
            _seams.RefreshCaptureSetup?.Invoke();
    }

    internal void ObserveFailure(string operation, Exception exception) =>
        Runner.Run(operation, _ => Task.FromException(exception), CancellationToken.None);

    internal void ReportFallback(string operation, Exception exception)
    {
        try
        {
            _seams.WriteDiagnostics(operation, exception);
        }
        catch (Exception)
        {
            // An emergency diagnostics sink must never fail back into the dispatcher or finalizer.
        }
    }

    internal static void TraceFailure(string operation, Exception exception)
    {
        try
        {
            Trace.WriteLine($"UI operation {operation} failed: {exception}");
        }
        catch (Exception)
        {
            // Custom trace listeners can throw; diagnostics must not replace the original failure.
        }
    }

    // Only these operations call SetBusy(true). A failure elsewhere must not clear the busy state of another operation.
    internal static bool OwnsBusy(string operation) => operation is
        "capture.load" or "capture.source-adapter" or "capture.startup" or "capture.decode" or
        "capture.register-profile" or "capture.apply-setup" or "capture.export-readable-log" or
        "output.export-points" or "output.export-heatmap" or "output.export-analysis";

    private static string FailureLabel(string operation) => operation switch
    {
        "capture.load" or "capture.source-adapter" or "capture.startup" => "Load failed",
        "capture.register-profile" => "Register profile failed",
        "capture.export-readable-log" => "Readable log export failed",
        "capture.version-selection" or "capture.version-confirm" or "capture.palm-confirm" or
        "capture.target-address-focus" or "capture.target-address-key" or "capture.apply-setup" or
        "capture.decode" => "Decode failed",
        "output.export-points" => "Point plot export failed",
        "output.export-heatmap" => "Heatmap export failed",
        "output.export-analysis" => "Analysis export failed",
        "output.export-replay" or "output.export-continue" => "Replay export failed",
        "output.preview-play-pause" => "Output preview failed",
        "paint.zoom" => "Zoom update failed",
        "review.copy-inspector" => "Inspector copy failed",
        "review.attach-evidence" => "Evidence attach failed",
        "review.save" => "Review save failed",
        "review.load" => "Review open failed",
        "workspace.selection" => "Workspace update failed",
        _ => $"UI operation {operation} failed",
    };
}
