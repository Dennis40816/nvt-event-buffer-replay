using System.Globalization;
using System.Security.Cryptography;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed record NvtRegisterProfile(
    string IcFamily,
    uint EventBufferBase,
    uint CommonBufferBase,
    uint HistoryBase)
{
    public NvtRegisterProfileIdentity Identity => NvtRegisterProfileIdentity.Create(this);
}

public sealed record NvtRegisterDescription(
    string Profiles,
    string ProfileResolution,
    string Region,
    string Name,
    uint Address,
    uint BaseAddress,
    uint Offset,
    string RawValue,
    string Meaning,
    string Readable,
    NvtFwCommand? FwCommand = null)
{
    public string StableName => NvtRegisterCatalog.StableName(Name);

    public IReadOnlyDictionary<string, string> ToSourceFields()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["register_profile"] = Profiles,
            ["register_profile_resolution"] = ProfileResolution,
            ["register_region"] = Region,
            ["register_name"] = StableName,
            ["register_display_name"] = Name,
            ["register_address"] = $"0x{Address:X}",
            ["register_base"] = $"0x{BaseAddress:X}",
            ["register_offset"] = $"0x{Offset:X2}",
            ["register_value"] = RawValue,
            ["register_meaning"] = Meaning,
            ["register_readable"] = Readable,
        };
        if (FwCommand is { } command)
        {
            fields["fw_command_code"] = $"0x{command.Code:X2}";
            fields["fw_command_name"] = command.Display;
            fields["fw_command_confirmed"] = command.Confirmed.ToString().ToLowerInvariant();
        }
        return fields;
    }
}

public static class NvtRegisterCatalog
{
    private const uint EventBufferLength = 128;
    private const uint HistoryLength = 128;
    private static readonly string[] SemanticFieldKeys =
    [
        "register_profile",
        "register_profile_resolution",
        "register_region",
        "register_display_name",
        "register_base",
        "register_value",
        "register_meaning",
        "register_readable",
        "fw_command_code",
        "fw_command_name",
        "fw_command_confirmed",
    ];

    public static IReadOnlyList<NvtRegisterProfile> Profiles { get; } =
    [
        new("51923", 0x94000, 0x941C0, 0x9ACA0),
        new("51926", 0x96A00, 0x97B9C, 0x9BCA0),
        new("51927", 0x99000, 0x8EC98, 0x99200),
        new("51929/51932", 0x80800, 0xA5200, 0x9D130),
        new("51950/51951", 0x80800, 0xAAD8C, 0xA445C),
    ];

    public static NvtRegisterProfileIdentity AutomaticProfileIdentity { get; } = CreateAutomaticProfileIdentity();

    public static SourceRecord Annotate(SourceRecord record, string? icFamily = null)
    {
        if (record.Address is not { } address || Describe(address, record.Operation, record.Data, icFamily) is not { } description)
            return record;

        var fields = new Dictionary<string, string>(record.SourceFields ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var field in description.ToSourceFields()) fields[field.Key] = field.Value;
        return record with { SourceFields = fields };
    }

    public static SourceRecord Reannotate(SourceRecord record, string? icFamily)
    {
        if (!string.IsNullOrWhiteSpace(icFamily) && FindProfile(icFamily) is null)
            throw new ArgumentException($"Unknown NVT register profile '{icFamily}'.", nameof(icFamily));

        var fields = new Dictionary<string, string>(record.SourceFields ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var hadCatalogAnnotation = fields.ContainsKey("register_profile_resolution");
        foreach (var key in SemanticFieldKeys) fields.Remove(key);
        if (hadCatalogAnnotation && !fields.ContainsKey("register_page_known"))
            fields.Remove("register_name");

        return Annotate(record with { SourceFields = fields }, string.IsNullOrWhiteSpace(icFamily) ? null : icFamily);
    }

    public static NvtRegisterProfile? FindProfile(string? icFamily) =>
        string.IsNullOrWhiteSpace(icFamily)
            ? null
            : Profiles.FirstOrDefault(profile => profile.IcFamily.Equals(icFamily, StringComparison.OrdinalIgnoreCase));

    public static NvtRegisterDescription? Describe(
        uint address,
        BusOperation operation,
        IReadOnlyList<byte> payload,
        string? icFamily = null)
    {
        var raw = Raw(payload);
        if (DescribeCommon(address, operation, payload, raw) is { } common) return common;

        var matches = MatchProfiles(address)
            .Where(match => icFamily is null || match.Profile.IcFamily.Equals(icFamily, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0) return null;
        var profiles = string.Join(" | ", matches.Select(match => match.Profile.IcFamily).Distinct(StringComparer.Ordinal));
        if (icFamily is null && matches.Select(match => match.Profile.IcFamily).Distinct(StringComparer.Ordinal).Skip(1).Any())
        {
            return new NvtRegisterDescription(
                profiles,
                "ambiguous",
                "Unresolved",
                "Profile Required",
                address,
                address,
                0,
                raw,
                "raw_only",
                $"Profile required · {profiles.Replace(" | ", " or ", StringComparison.Ordinal)}");
        }

        var first = matches[0];
        var (name, meaning, command) = DescribeRegion(first.Region, first.Offset, operation, payload);
        var readable = meaning == "raw_only" ? $"{name} · {raw}" : $"{name} · {meaning}";
        return new NvtRegisterDescription(
            profiles,
            "resolved",
            first.Region,
            name,
            address,
            first.BaseAddress,
            first.Offset,
            raw,
            meaning,
            readable,
            command);
    }

    public static NvtRegisterDescription? DescribeForProfile(
        uint address,
        BusOperation operation,
        IReadOnlyList<byte> payload,
        NvtRegisterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var raw = Raw(payload);
        if (DescribeCommon(address, operation, payload, raw) is { } common) return common;

        var match = MatchProfiles(address, [profile]).FirstOrDefault();
        if (match is null) return null;
        var (name, meaning, command) = DescribeRegion(match.Region, match.Offset, operation, payload);
        var readable = meaning == "raw_only" ? $"{name} · {raw}" : $"{name} · {meaning}";
        return new NvtRegisterDescription(
            profile.IcFamily,
            "resolved",
            match.Region,
            name,
            address,
            match.BaseAddress,
            match.Offset,
            raw,
            meaning,
            readable,
            command);
    }

    public static string EventBufferOffsetName(byte offset) => StableName(DescribeEventBufferOffset(offset).Name);

    internal static string StableName(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        var separatorPending = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && result.Length > 0) result.Append('_');
                result.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }
        return result.ToString();
    }

    private static IEnumerable<ProfileMatch> MatchProfiles(uint address) => MatchProfiles(address, Profiles);

    private static IEnumerable<ProfileMatch> MatchProfiles(
        uint address,
        IEnumerable<NvtRegisterProfile> profiles)
    {
        foreach (var profile in profiles)
        {
            if (address >= profile.EventBufferBase && address < profile.EventBufferBase + EventBufferLength)
                yield return new ProfileMatch(profile, "Event Buffer", profile.EventBufferBase, address - profile.EventBufferBase);
            if (address == profile.CommonBufferBase)
                yield return new ProfileMatch(profile, "Common Buffer", profile.CommonBufferBase, 0);
            if (address >= profile.HistoryBase && address < profile.HistoryBase + HistoryLength)
                yield return new ProfileMatch(profile, "History", profile.HistoryBase, address - profile.HistoryBase);
        }
    }

    private static (string Name, string Meaning, NvtFwCommand? Command) DescribeRegion(
        string region,
        uint offset,
        BusOperation operation,
        IReadOnlyList<byte> payload)
    {
        if (region == "Event Buffer") return DescribeEventBufferOffset(offset, operation, payload);
        var name = offset == 0 ? region : $"{region} +0x{offset:X2}";
        return (name, "raw_only", null);
    }

    private static (string Name, string Meaning, NvtFwCommand? Command) DescribeEventBufferOffset(
        uint offset,
        BusOperation operation = BusOperation.Unknown,
        IReadOnlyList<byte>? payload = null)
    {
        payload ??= Array.Empty<byte>();
        if (offset == 0x50)
        {
            var command = operation == BusOperation.Write ? NvtFwCommandParser.Parse(payload) : null;
            return ("FW Command", command?.Display ?? "raw_only", command);
        }
        if (offset == 0x60)
        {
            var meaning = payload.Count > 0 && payload[0] == 0xA3 ? "Normal Run" : "raw_only";
            return ("FW State", meaning, null);
        }
        return offset switch
        {
            0x00 => ("Event Buffer", "raw_only", null),
            0x70 => ("Frame Counter", "raw_only", null),
            0x76 => ("DP Version", "raw_only", null),
            0x78 => ("TP FW Version", "raw_only", null),
            _ => ($"Event Buffer +0x{offset:X2}", "raw_only", null),
        };
    }

    private static string Raw(IReadOnlyList<byte> payload) =>
        payload.Count == 0
            ? "—"
            : string.Join(' ', payload.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static NvtRegisterProfileIdentity CreateAutomaticProfileIdentity()
    {
        var canonical = string.Join('\n', Profiles.Select(profile => profile.Identity.ContentHash));
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical));
        return new NvtRegisterProfileIdentity("AUTOMATIC", Convert.ToHexStringLower(hash));
    }

    private static NvtRegisterDescription? DescribeCommon(
        uint address,
        BusOperation operation,
        IReadOnlyList<byte> payload,
        string raw)
    {
        if (address == 0xFF0FE)
        {
            var confirmedReset = operation == BusOperation.Write && payload.Count > 0 && payload[0] == 0x69;
            var resetMeaning = confirmedReset ? "Software Reset" : "raw_only";
            return new NvtRegisterDescription(
                "Common", "common", "Control", "Reset Control", address, address, 0, raw, resetMeaning,
                confirmedReset ? "Reset Control · Software Reset" : $"Reset Control · {raw}");
        }

        if (address is >= 0xFF000 and <= 0xFF003)
        {
            var offset = address - 0xFF000;
            return new NvtRegisterDescription(
                "Common", "common", "Identification", "Chip ID", address, 0xFF000, offset, raw, "raw_only",
                $"Chip ID +0x{offset:X2} · {raw}");
        }

        return null;
    }

    private sealed record ProfileMatch(NvtRegisterProfile Profile, string Region, uint BaseAddress, uint Offset);
}
