using System.Globalization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed record NvtRegisterProfile(
    string IcFamily,
    uint EventBufferBase,
    uint CommonBufferBase,
    uint HistoryBase);

public sealed record NvtRegisterDescription(
    string Profiles,
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
    public IReadOnlyDictionary<string, string> ToSourceFields()
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["register_profile"] = Profiles,
            ["register_region"] = Region,
            ["register_name"] = NvtRegisterCatalog.StableName(Name),
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

    public static IReadOnlyList<NvtRegisterProfile> Profiles { get; } =
    [
        new("51923", 0x94000, 0x941C0, 0x9ACA0),
        new("51926", 0x96A00, 0x97B9C, 0x9BCA0),
        new("51927", 0x99000, 0x8EC98, 0x99200),
        new("51929/51932", 0x80800, 0xA5200, 0x9D130),
        new("51950/51951", 0x80800, 0xAAD8C, 0xA445C),
    ];

    public static SourceRecord Annotate(SourceRecord record)
    {
        if (record.Address is not { } address || Describe(address, record.Operation, record.Data) is not { } description)
            return record;

        var fields = new Dictionary<string, string>(record.SourceFields ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var field in description.ToSourceFields()) fields[field.Key] = field.Value;
        return record with { SourceFields = fields };
    }

    public static NvtRegisterDescription? Describe(uint address, BusOperation operation, IReadOnlyList<byte> payload)
    {
        var raw = Raw(payload);
        if (address == 0xFF0FE)
        {
            var confirmedReset = operation == BusOperation.Write && payload.Count > 0 && payload[0] == 0x69;
            var resetMeaning = confirmedReset ? "Software Reset" : "raw_only";
            return new NvtRegisterDescription(
                "Common", "Control", "Reset Control", address, address, 0, raw, resetMeaning,
                confirmedReset ? "Reset Control · Software Reset" : $"Reset Control · {raw}");
        }

        if (address is >= 0xFF000 and <= 0xFF003)
        {
            var offset = address - 0xFF000;
            return new NvtRegisterDescription(
                "Common", "Identification", "Chip ID", address, 0xFF000, offset, raw, "raw_only",
                $"Chip ID +0x{offset:X2} · {raw}");
        }

        var matches = MatchProfiles(address).ToArray();
        if (matches.Length == 0) return null;
        var first = matches[0];
        var profiles = string.Join(" | ", matches.Select(match => match.Profile.IcFamily).Distinct(StringComparer.Ordinal));
        var (name, meaning, command) = DescribeRegion(first.Region, first.Offset, operation, payload);
        var readable = meaning == "raw_only" ? $"{name} · {raw}" : $"{name} · {meaning}";
        return new NvtRegisterDescription(
            profiles,
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

    private static IEnumerable<ProfileMatch> MatchProfiles(uint address)
    {
        foreach (var profile in Profiles)
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

    private sealed record ProfileMatch(NvtRegisterProfile Profile, string Region, uint BaseAddress, uint Offset);
}
