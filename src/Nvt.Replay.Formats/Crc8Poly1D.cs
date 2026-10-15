namespace Nvt.Replay.Formats;

public static class Crc8Poly1D
{
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
