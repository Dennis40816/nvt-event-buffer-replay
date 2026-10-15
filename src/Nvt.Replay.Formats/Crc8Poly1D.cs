namespace Nvt.Replay.Formats;

/// <summary>Computes CRC-8 with polynomial 0x1D and initial value 0.</summary>
public static class Crc8Poly1D
{
    /// <summary>Returns the CRC-8 of <paramref name="data"/>, processed most significant bit first with no final XOR.</summary>
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
