using Nvt.Replay.Core;

namespace Nvt.Replay.Formats.Common;

/// <summary>Provides CRC-8 profiles and checksum computation for Common Event Buffer data.</summary>
public static class CommonCrc8
{
    /// <summary>Gets CRC profiles and their evidence status for supported Common Event Buffer versions.</summary>
    public static IReadOnlyDictionary<CommonEventBufferVersion, CommonCrcProfile> Profiles { get; } =
        new Dictionary<CommonEventBufferVersion, CommonCrcProfile>
        {
            [CommonEventBufferVersion.V82] = Profile(EvidenceStatus.Provisional),
            [CommonEventBufferVersion.V83] = Profile(EvidenceStatus.Verified),
            [CommonEventBufferVersion.V84] = Profile(EvidenceStatus.Verified),
            [CommonEventBufferVersion.V85] = Profile(EvidenceStatus.Provisional),
        };

    /// <summary>Computes the CRC-8 checksum of the supplied bytes using polynomial 0x1D.</summary>
    public static byte Compute(ReadOnlySpan<byte> data)
        => Crc8Poly1D.Compute(data);

    private static CommonCrcProfile Profile(EvidenceStatus status) =>
        new(0x1D, 0, false, false, 0, status);
}
