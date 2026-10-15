using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

/// <summary>Writes the hotspot heatmap of an analysis report as a PNG file.</summary>
public sealed class HeatmapPngWriter
{
    /// <summary>Writes the report's hotspot heatmap to the output path at the pixel size set in the report manifest.</summary>
    public Task WriteAsync(
        string outputPath,
        CaptureAnalysisReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(report);
        return Nvt.Core.IO.AtomicOutput.WriteAsync(
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
