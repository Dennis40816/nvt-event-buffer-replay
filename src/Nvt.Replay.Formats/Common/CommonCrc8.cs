using Nvt.Replay.Core;

namespace Nvt.Replay.Formats.Common;

public static class CommonCrc8
{
    public static IReadOnlyDictionary<CommonEventBufferVersion, CommonCrcProfile> Profiles { get; } =
        new Dictionary<CommonEventBufferVersion, CommonCrcProfile>
        {
            [CommonEventBufferVersion.V82] = Profile(EvidenceStatus.Provisional),
            [CommonEventBufferVersion.V83] = Profile(EvidenceStatus.Verified),
            [CommonEventBufferVersion.V84] = Profile(EvidenceStatus.Verified),
            [CommonEventBufferVersion.V85] = Profile(EvidenceStatus.Provisional),
        };

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

    private static CommonCrcProfile Profile(EvidenceStatus status) =>
        new(0x1D, 0, false, false, 0, status);
}
