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
    private const int PreviewWidth = 640;
    private const int PreviewHeight = 360;
    private WriteableBitmap? bitmap;

    public void Show(ReplayScene scene, ReplayRenderMode mode)
    {
        var rgb = ReplayFrameRenderer.RenderRgb(scene, PreviewWidth, PreviewHeight, mode);
        var rgba = new byte[PreviewWidth * PreviewHeight * 4];
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
                new PixelSize(PreviewWidth, PreviewHeight),
                new Vector(96, 96),
                PreviewWidth * 4);
            bitmap?.Dispose();
            bitmap = next;
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
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var scale = Math.Min(Bounds.Width / PreviewWidth, Bounds.Height / PreviewHeight);
        var width = PreviewWidth * scale;
        var height = PreviewHeight * scale;
        var destination = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        context.DrawImage(bitmap, new Rect(0, 0, PreviewWidth, PreviewHeight), destination);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Clear();
        base.OnDetachedFromVisualTree(e);
    }
}
