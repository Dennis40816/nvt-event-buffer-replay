using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Nvt.Replay.Sources;

public sealed record NvtRegisterProfileIdentity(string Name, string ContentHash)
{
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
