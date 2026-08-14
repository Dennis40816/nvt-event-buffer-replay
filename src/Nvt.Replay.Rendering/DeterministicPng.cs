using System.Buffers.Binary;
using Nvt.Replay.Analysis;

namespace Nvt.Replay.Rendering;

internal static class DeterministicPng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task WriteHeatmapAsync(
        Stream output,
        AnalysisHotspot hotspot,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        if (hotspot.Counts.Count != hotspot.Columns * hotspot.Rows)
            throw new InvalidDataException("Hotspot cell count does not match its dimensions.");
        var intensity = Blur(hotspot);
        var maximum = Math.Max(1, intensity.Max());
        var rgb = new byte[height * width * 3];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowOffset = y * width * 3;
            var gridY = Math.Min(hotspot.Rows - 1, y * hotspot.Rows / height);
            for (var x = 0; x < width; x++)
            {
                var gridX = Math.Min(hotspot.Columns - 1, x * hotspot.Columns / width);
                var density = Math.Log(1 + intensity[(gridY * hotspot.Columns) + gridX]) / Math.Log(1 + maximum);
                var (red, green, blue) = Color(density);
                var offset = rowOffset + (x * 3);
                rgb[offset] = red;
                rgb[offset + 1] = green;
                rgb[offset + 2] = blue;
            }
        }
        await WriteRgbAsync(output, rgb, width, height, cancellationToken);
    }

    public static async Task WriteRgbAsync(Stream output, byte[] rgb, int width, int height, CancellationToken cancellationToken)
    {
        if (rgb.Length != width * height * 3) throw new ArgumentException("RGB data does not match the requested dimensions.", nameof(rgb));
        await output.WriteAsync(Signature, cancellationToken);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 2;
        await WriteChunkAsync(output, "IHDR"u8.ToArray(), header, cancellationToken);
        var pixels = new byte[height * (1 + (width * 3))];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(rgb, y * width * 3, pixels, (y * (1 + (width * 3))) + 1, width * 3);
        await WriteChunkAsync(output, "IDAT"u8.ToArray(), ZlibStored(pixels), cancellationToken);
        await WriteChunkAsync(output, "IEND"u8.ToArray(), [], cancellationToken);
    }

    private static (byte Red, byte Green, byte Blue) Color(double value)
    {
        if (value <= 0) return (10, 13, 15);
        var clamped = Math.Clamp(value, 0, 1);
        var red = (byte)Math.Round(38 + (217 * Math.Pow(clamped, 1.7)));
        var green = (byte)Math.Round(109 + (108 * clamped));
        var blue = (byte)Math.Round(148 - (116 * clamped));
        return (red, green, blue);
    }

    private static double[] Blur(AnalysisHotspot hotspot)
    {
        var result = new double[hotspot.Counts.Count];
        for (var y = 0; y < hotspot.Rows; y++)
        {
            for (var x = 0; x < hotspot.Columns; x++)
            {
                double value = 0;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var sourceY = y + dy;
                    if (sourceY < 0 || sourceY >= hotspot.Rows) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var sourceX = x + dx;
                        if (sourceX < 0 || sourceX >= hotspot.Columns) continue;
                        var weight = (2 - Math.Abs(dx)) * (2 - Math.Abs(dy));
                        value += hotspot.Counts[(sourceY * hotspot.Columns) + sourceX] * weight;
                    }
                }
                result[(y * hotspot.Columns) + x] = value;
            }
        }
        return result;
    }

    private static byte[] ZlibStored(byte[] data)
    {
        using var output = new MemoryStream(data.Length + 32 + (data.Length / ushort.MaxValue * 5));
        output.WriteByte(0x78);
        output.WriteByte(0x01);
        var offset = 0;
        while (offset < data.Length)
        {
            var length = Math.Min(ushort.MaxValue, data.Length - offset);
            output.WriteByte(offset + length == data.Length ? (byte)1 : (byte)0);
            output.WriteByte((byte)length);
            output.WriteByte((byte)(length >> 8));
            var complement = (ushort)~length;
            output.WriteByte((byte)complement);
            output.WriteByte((byte)(complement >> 8));
            output.Write(data, offset, length);
            offset += length;
        }
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, Adler32(data));
        output.Write(checksum);
        return output.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (var value in data)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }
        return (b << 16) | a;
    }

    private static async Task WriteChunkAsync(Stream output, byte[] type, byte[] data, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        await output.WriteAsync(length, cancellationToken);
        await output.WriteAsync(type, cancellationToken);
        await output.WriteAsync(data, cancellationToken);
        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, type.Length);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(crcInput));
        await output.WriteAsync(length, cancellationToken);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}
