using Nvt.Replay.Analysis;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia;

/// <summary>
/// Hooks the window tests set to observe or replace UI side effects.
/// All members are null in production. The window reads them on the UI thread.
/// </summary>
internal sealed class MainWindowTestSeams
{
    public Action<CaptureLoadProgress>? CaptureLoadProgressObserver { get; set; }
    public Action<CaptureDecodeProgress>? CaptureDecodeProgressObserver { get; set; }
    public Action<string>? OutputFolderRevealAction { get; set; }
    public Func<CancellationToken, Task<FfmpegProbeResult>>? FfmpegProbe { get; set; }
    public Action<string, int>? SourceLocationOpenAction { get; set; }
}
