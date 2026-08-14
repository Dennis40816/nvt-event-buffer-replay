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
        => Crc8Poly1D.Compute(data);

    private static CommonCrcProfile Profile(EvidenceStatus status) =>
        new(0x1D, 0, false, false, 0, status);
}
