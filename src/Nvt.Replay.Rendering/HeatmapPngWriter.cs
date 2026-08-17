using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

public sealed class HeatmapPngWriter
{
    public Task WriteAsync(
        string outputPath,
        CaptureAnalysisReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(report);
        return AtomicOutput.WriteAsync(
            outputPath,
            (stream, token) => DeterministicPng.WriteHeatmapAsync(
                stream,
                report.Hotspot,
                report.Manifest.HeatmapPixelWidth,
                report.Manifest.HeatmapPixelHeight,
                token),
            cancellationToken);
    }
}
