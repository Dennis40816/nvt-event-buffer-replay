using System.Globalization;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

public enum RegisterActivityKind
{
    Read,
    ChangedRead,
    Write,
    Command,
    Reset,
    PageSwitch,
    Ambiguous,
}

public sealed record RegisterActivityEntry(
    SourceRecord Record,
    RegisterActivityKind Kind,
    string Profile,
    string Resolution,
    string Region,
    string Register,
    string Meaning,
    string RawValue,
    bool HasPreviousSample,
    bool ChangedFromPreviousSample)
{
    public bool IsKnown => Resolution is "resolved" or "common" or "inferred";
    public bool IsAmbiguous => Resolution == "ambiguous";
}

public static class RegisterActivityProjector
{
    public static IReadOnlyList<RegisterActivityEntry> Project(IEnumerable<SourceRecord> records)
        => Project(records, null);

    public static IReadOnlyList<RegisterActivityEntry> Project(
        IEnumerable<SourceRecord> records,
        RegisterAnnotationProjection? projection)
    {
        ArgumentNullException.ThrowIfNull(records);
        var result = new List<RegisterActivityEntry>();
        var previousReads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            if (record.Operation == BusOperation.Write &&
                record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase) &&
                NvtRegisterTracker.TryGetPageSelection(record.Data, out var page))
            {
                var pageText = $"0x{page:X}";
                result.Add(new RegisterActivityEntry(
                    record,
                    RegisterActivityKind.PageSwitch,
                    projection?.Profile?.IcFamily ?? "Common",
                    "common",
                    "I²C control",
                    $"Switch page · {pageText}",
                    "Page select",
                    Raw(record.Data),
                    HasPreviousSample: false,
                    ChangedFromPreviousSample: false));
                continue;
            }

            var fields = projection is null
                ? record.SourceFields
                : projection.TryGet(record, out var annotation) ? annotation.Fields : null;
            if (fields is null || !fields.TryGetValue("register_readable", out var readable)) continue;

            var profile = fields.GetValueOrDefault("register_profile") ?? "Unknown";
            var resolution = fields.GetValueOrDefault("register_profile_resolution") ?? "unknown";
            var region = fields.GetValueOrDefault("register_region") ?? "Unknown";
            var meaning = fields.GetValueOrDefault("register_meaning") ?? "raw_only";
            var raw = fields.GetValueOrDefault("register_value") ?? Raw(record.Data);
            var key = $"{profile}:{record.Address?.ToString("X", CultureInfo.InvariantCulture) ?? fields.GetValueOrDefault("register_name")}";
            byte[]? previous = null;
            var hasPrevious = record.Operation == BusOperation.Read && previousReads.TryGetValue(key, out previous);
            var changed = hasPrevious && !record.Data.SequenceEqual(previous!);
            if (record.Operation == BusOperation.Read) previousReads[key] = record.Data.ToArray();

            var kind = resolution == "ambiguous"
                ? RegisterActivityKind.Ambiguous
                : meaning == "Software Reset"
                    ? RegisterActivityKind.Reset
                    : fields.ContainsKey("fw_command_code")
                        ? RegisterActivityKind.Command
                        : record.Operation == BusOperation.Write
                            ? RegisterActivityKind.Write
                            : changed
                                ? RegisterActivityKind.ChangedRead
                                : RegisterActivityKind.Read;
            result.Add(new RegisterActivityEntry(
                record,
                kind,
                profile,
                resolution,
                region,
                readable,
                meaning,
                raw,
                hasPrevious,
                changed));
        }
        return result;
    }

    private static string Raw(IReadOnlyList<byte> payload) =>
        payload.Count == 0
            ? "—"
            : string.Join(' ', payload.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
}
