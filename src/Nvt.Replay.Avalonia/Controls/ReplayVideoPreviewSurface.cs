using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.Controls;

public sealed class ReplayVideoPreviewSurface : Control
{
    private WriteableBitmap? bitmap;
    private PixelSize bitmapSize;
    private byte[]? rgbBuffer;
    private byte[]? rgbaBuffer;

    internal int RgbRenderCount { get; private set; }
    internal bool HasBitmap => bitmap is not null;

    public ReplayVideoPreviewSurface()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public void Show(ReplayScene scene, ReplayRenderSettings settings, int width, int height)
    {
        RgbRenderCount++;
        EnsureBitmap(width, height);
        ReplayFrameRenderer.RenderRgb(scene, width, height, settings, rgbBuffer!);
        for (int source = 0, destination = 0; source < rgbBuffer!.Length; source += 3, destination += 4)
        {
            rgbaBuffer![destination] = rgbBuffer[source];
            rgbaBuffer[destination + 1] = rgbBuffer[source + 1];
            rgbaBuffer[destination + 2] = rgbBuffer[source + 2];
            rgbaBuffer[destination + 3] = byte.MaxValue;
        }

        using (var frameBuffer = bitmap!.Lock())
        {
            var sourceStride = width * 4;
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(
                    rgbaBuffer!,
                    row * sourceStride,
                    IntPtr.Add(frameBuffer.Address, row * frameBuffer.RowBytes),
                    sourceStride);
            }
        }
        InvalidateVisual();
    }

    private void EnsureBitmap(int width, int height)
    {
        var nextSize = new PixelSize(width, height);
        if (bitmap is not null && bitmapSize == nextSize) return;

        bitmap?.Dispose();
        bitmap = new WriteableBitmap(
            nextSize,
            new Vector(96, 96),
            PixelFormat.Rgba8888,
            AlphaFormat.Unpremul);
        bitmapSize = nextSize;
        rgbBuffer = new byte[checked(width * height * 3)];
        rgbaBuffer = new byte[checked(width * height * 4)];
    }

    public void Clear()
    {
        bitmap?.Dispose();
        bitmap = null;
        bitmapSize = default;
        rgbBuffer = null;
        rgbaBuffer = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (bitmap is null || bitmapSize.Width <= 0 || bitmapSize.Height <= 0 || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var scale = Math.Min(Bounds.Width / bitmapSize.Width, Bounds.Height / bitmapSize.Height);
        var width = bitmapSize.Width * scale;
        var height = bitmapSize.Height * scale;
        var destination = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        context.DrawImage(bitmap, new Rect(0, 0, bitmapSize.Width, bitmapSize.Height), destination);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Clear();
        base.OnDetachedFromVisualTree(e);
    }
}
