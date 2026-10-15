namespace Nvt.Replay.Formats;

/// <summary>Computes CRC-8 checksums using polynomial 0x1D and an initial value of zero.</summary>
public static class Crc8Poly1D
{
    /// <summary>Computes the CRC-8 checksum of the supplied bytes using polynomial 0x1D and an initial value of zero.</summary>
    public static byte Compute(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x1D : crc << 1);
            }
        }

        return crc;
    }
}
