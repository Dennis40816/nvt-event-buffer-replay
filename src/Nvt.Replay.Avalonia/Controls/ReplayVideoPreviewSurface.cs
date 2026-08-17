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

    public void Show(ReplayScene scene, ReplayRenderSettings settings, int width, int height)
    {
        var rgb = ReplayFrameRenderer.RenderRgb(scene, width, height, settings);
        var rgba = new byte[width * height * 4];
        for (int source = 0, destination = 0; source < rgb.Length; source += 3, destination += 4)
        {
            rgba[destination] = rgb[source];
            rgba[destination + 1] = rgb[source + 1];
            rgba[destination + 2] = rgb[source + 2];
            rgba[destination + 3] = byte.MaxValue;
        }
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var next = new WriteableBitmap(
                PixelFormat.Rgba8888,
                AlphaFormat.Unpremul,
                handle.AddrOfPinnedObject(),
                new PixelSize(width, height),
                new Vector(96, 96),
                width * 4);
            bitmap?.Dispose();
            bitmap = next;
            bitmapSize = new PixelSize(width, height);
        }
        finally
        {
            handle.Free();
        }
        InvalidateVisual();
    }

    public void Clear()
    {
        bitmap?.Dispose();
        bitmap = null;
        bitmapSize = default;
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
