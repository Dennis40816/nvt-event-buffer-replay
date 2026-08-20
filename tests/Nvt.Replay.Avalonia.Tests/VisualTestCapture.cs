using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Nvt.Replay.Avalonia.Tests;

internal static class VisualTestCapture
{
    private static readonly Vector CaptureDpi = new(96, 96);

    public static void Stabilize(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.InvalidateMeasure();
        window.InvalidateArrange();
        window.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();

        // The first synchronous render primes font glyphs and any lazily-created
        // control templates. The captured render below then observes a settled tree.
        using var warmup = CreateBitmap(window);
        warmup.Render(window);
        Dispatcher.UIThread.RunJobs();
    }

    public static string CaptureHash(Window window, string artifactName)
    {
        Stabilize(window);

        using var frame = CreateBitmap(window);
        frame.Render(window);
        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);

        var artifactDirectory = Environment.GetEnvironmentVariable("NVT_UI_AUDIT_DIR");
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            Directory.CreateDirectory(artifactDirectory);
            frame.Save(Path.Combine(artifactDirectory, artifactName), PngBitmapEncoderOptions.Default);
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static RenderTargetBitmap CreateBitmap(Window window)
    {
        var width = Math.Max(1, (int)Math.Round(window.Bounds.Width, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(window.Bounds.Height, MidpointRounding.AwayFromZero));
        return new RenderTargetBitmap(new PixelSize(width, height), CaptureDpi);
    }
}
