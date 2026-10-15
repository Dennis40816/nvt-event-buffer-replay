using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nvt.Replay.Sources;

/// <summary>Identifies a register profile by its normalized IC family name and a hash of its identifying fields.</summary>
/// <param name="Name">The IC family name, trimmed and converted to upper case.</param>
/// <param name="ContentHash">The lowercase SHA-256 hex over the family name and the event, common, and history base addresses.</param>
public sealed record NvtRegisterProfileIdentity(string Name, string ContentHash)
{
    /// <summary>Creates an identity from the normalized IC family and a SHA-256 hash of its name and base addresses.</summary>
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
