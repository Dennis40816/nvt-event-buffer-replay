using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SkiaSharp;

namespace Nvt.Replay.Avalonia.Tests;

internal static class VisualTestCapture
{
    private static readonly Vector CaptureDpi = new(96, 96);
    private const string CandidateDirectoryVariable = "NVT_UI_CANDIDATE_DIR";
    private const string CandidateModeVariable = "NVT_UI_CANDIDATE_MODE";
    private const string AuditDirectoryVariable = "NVT_UI_AUDIT_DIR";

    public static bool CandidateMatrixEnabled
    {
        get
        {
            var directory = Environment.GetEnvironmentVariable(CandidateDirectoryVariable);
            var mode = Environment.GetEnvironmentVariable(CandidateModeVariable);
            return !string.IsNullOrWhiteSpace(directory) && mode is "capture" or "verify";
        }
    }

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
        var png = CapturePng(window);

        var artifactDirectory = Environment.GetEnvironmentVariable(AuditDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(artifactDirectory))
        {
            Directory.CreateDirectory(artifactDirectory);
            File.WriteAllBytes(Path.Combine(artifactDirectory, artifactName), png);
        }

        return Convert.ToHexString(SHA256.HashData(png));
    }

    public static void ProcessCandidate(Window window, string artifactName)
    {
        var png = CapturePng(window);
        var candidateDirectory = Environment.GetEnvironmentVariable(CandidateDirectoryVariable);
        var mode = Environment.GetEnvironmentVariable(CandidateModeVariable);
        if (string.IsNullOrWhiteSpace(candidateDirectory) || mode is not ("capture" or "verify"))
            throw new InvalidOperationException("Candidate capture requires an explicit candidate directory and capture/verify mode.");

        var candidatePath = Path.Combine(candidateDirectory, artifactName);
        if (mode == "capture")
        {
            Directory.CreateDirectory(candidateDirectory);
            File.WriteAllBytes(candidatePath, png);
            return;
        }

        if (!File.Exists(candidatePath))
        {
            throw new InvalidOperationException(
                $"Missing UI snapshot candidate '{candidatePath}'. " +
                "Run scripts/capture-ui-snapshot-candidates.ps1 -Capture before verification.");
        }

        var candidate = File.ReadAllBytes(candidatePath);
        if (candidate.AsSpan().SequenceEqual(png))
            return;

        var auditDirectory = Environment.GetEnvironmentVariable(AuditDirectoryVariable);
        if (string.IsNullOrWhiteSpace(auditDirectory))
            auditDirectory = Path.Combine(Path.GetTempPath(), "nvt-ui-candidate-diff");
        Directory.CreateDirectory(auditDirectory);

        var actualPath = Path.Combine(auditDirectory, $"actual-{artifactName}");
        var diffPath = Path.Combine(auditDirectory, $"diff-{artifactName}");
        var metricsPath = Path.Combine(auditDirectory, $"metrics-{Path.GetFileNameWithoutExtension(artifactName)}.txt");
        File.WriteAllBytes(actualPath, png);
        var metrics = WriteDiff(candidate, png, diffPath);
        File.WriteAllText(metricsPath, metrics.ToString());

        throw new InvalidOperationException(
            $"UI snapshot candidate changed: '{artifactName}'. {metrics} " +
            $"Actual: '{actualPath}'. Diff: '{diffPath}'. " +
            "A mismatch never auto-passes; review the diff before recapturing the candidate.");
    }

    private static RenderTargetBitmap CreateBitmap(Window window)
    {
        var width = Math.Max(1, (int)Math.Round(window.Bounds.Width, MidpointRounding.AwayFromZero));
        var height = Math.Max(1, (int)Math.Round(window.Bounds.Height, MidpointRounding.AwayFromZero));
        return new RenderTargetBitmap(new PixelSize(width, height), CaptureDpi);
    }

    private static byte[] CapturePng(Window window)
    {
        Stabilize(window);

        using var frame = CreateBitmap(window);
        frame.Render(window);
        using var stream = new MemoryStream();
        frame.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    private static VisualDiffMetrics WriteDiff(byte[] expectedPng, byte[] actualPng, string diffPath)
    {
        using var expectedBitmap = SKBitmap.Decode(expectedPng) ??
            throw new InvalidOperationException("The UI snapshot candidate is not a readable PNG.");
        using var actual = SKBitmap.Decode(actualPng) ??
            throw new InvalidOperationException("The actual UI snapshot is not a readable PNG.");

        if (expectedBitmap.Width != actual.Width || expectedBitmap.Height != actual.Height)
        {
            using var sizeDiff = new SKBitmap(
                expectedBitmap.Width + actual.Width,
                Math.Max(expectedBitmap.Height, actual.Height),
                SKColorType.Rgba8888,
                SKAlphaType.Premul);
            using (var canvas = new SKCanvas(sizeDiff))
            {
                canvas.Clear(SKColors.Magenta);
                canvas.DrawBitmap(expectedBitmap, 0, 0);
                canvas.DrawBitmap(actual, expectedBitmap.Width, 0);
            }
            using var sizeDiffImage = SKImage.FromBitmap(sizeDiff);
            using var sizeDiffData = sizeDiffImage.Encode(SKEncodedImageFormat.Png, 100);
            using var sizeDiffOutput = File.Create(diffPath);
            sizeDiffData.SaveTo(sizeDiffOutput);

            var comparedPixelCount = Math.Max(
                (long)expectedBitmap.Width * expectedBitmap.Height,
                (long)actual.Width * actual.Height);
            return new VisualDiffMetrics(
                expectedBitmap.Width,
                expectedBitmap.Height,
                actual.Width,
                actual.Height,
                comparedPixelCount,
                comparedPixelCount,
                1,
                1,
                1,
                255);
        }

        using var diff = new SKBitmap(actual.Width, actual.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        long changedPixels = 0;
        long significantPixels = 0;
        long channelDeltaTotal = 0;
        var maxChannelDelta = 0;
        var pixelCount = (long)actual.Width * actual.Height;

        for (var y = 0; y < actual.Height; y++)
        {
            for (var x = 0; x < actual.Width; x++)
            {
                var expected = expectedBitmap.GetPixel(x, y);
                var observed = actual.GetPixel(x, y);
                var redDelta = Math.Abs(expected.Red - observed.Red);
                var greenDelta = Math.Abs(expected.Green - observed.Green);
                var blueDelta = Math.Abs(expected.Blue - observed.Blue);
                var alphaDelta = Math.Abs(expected.Alpha - observed.Alpha);
                var localMax = Math.Max(Math.Max(redDelta, greenDelta), Math.Max(blueDelta, alphaDelta));
                if (localMax > 0)
                    changedPixels++;
                if (localMax > 8)
                    significantPixels++;
                maxChannelDelta = Math.Max(maxChannelDelta, localMax);
                channelDeltaTotal += redDelta + greenDelta + blueDelta + alphaDelta;

                if (localMax == 0)
                {
                    var gray = (byte)((observed.Red + observed.Green + observed.Blue) / 6);
                    diff.SetPixel(x, y, new SKColor(gray, gray, gray, 255));
                }
                else
                {
                    var intensity = (byte)Math.Clamp(96 + localMax, 96, 255);
                    diff.SetPixel(x, y, new SKColor(255, (byte)(255 - intensity), intensity, 255));
                }
            }
        }

        using var image = SKImage.FromBitmap(diff);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var output = File.Create(diffPath);
        data.SaveTo(output);

        return new VisualDiffMetrics(
            expectedBitmap.Width,
            expectedBitmap.Height,
            actual.Width,
            actual.Height,
            changedPixels,
            significantPixels,
            changedPixels / (double)pixelCount,
            significantPixels / (double)pixelCount,
            channelDeltaTotal / (double)(pixelCount * 4 * 255),
            maxChannelDelta);
    }

    private readonly record struct VisualDiffMetrics(
        int ExpectedWidth,
        int ExpectedHeight,
        int ActualWidth,
        int ActualHeight,
        long ChangedPixels,
        long SignificantPixels,
        double ChangedRatio,
        double SignificantRatio,
        double NormalizedMeanAbsoluteError,
        int MaxChannelDelta)
    {
        public override string ToString() =>
            $"expected={ExpectedWidth}x{ExpectedHeight}, actual={ActualWidth}x{ActualHeight}, " +
            $"changed={ChangedPixels} ({ChangedRatio:P4}), significant(>8)={SignificantPixels} ({SignificantRatio:P4}), " +
            $"NMAE={NormalizedMeanAbsoluteError:F8}, max-channel-delta={MaxChannelDelta}.";
    }
}
