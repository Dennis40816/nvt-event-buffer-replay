using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nvt.Replay.Sources;

/// <summary>Identifies a register profile by its name and canonical content hash.</summary>
/// <param name="Name">The name identifying the register profile.</param>
/// <param name="ContentHash">The hash identifying the register profile content.</param>
public sealed record NvtRegisterProfileIdentity(string Name, string ContentHash)
{
    /// <summary>Creates an identity using the normalized IC family and a SHA-256 hash of its name and buffer base addresses.</summary>
    public static NvtRegisterProfileIdentity Create(NvtRegisterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalizedName = profile.IcFamily.Trim().ToUpperInvariant();
        var canonical = string.Join(
            '\n',
            "nvt-register-profile-v1",
            normalizedName,
            profile.EventBufferBase.ToString("X8", CultureInfo.InvariantCulture),
            profile.CommonBufferBase.ToString("X8", CultureInfo.InvariantCulture),
            profile.HistoryBase.ToString("X8", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new NvtRegisterProfileIdentity(normalizedName, Convert.ToHexStringLower(hash));
    }
}
