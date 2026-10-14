using System.Globalization;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

/// <summary>Classifies register activity entries produced from source records.</summary>
public enum RegisterActivityKind
{
    /// <summary>A register sample without a detected change or a more specific classification.</summary>
    Read,
    /// <summary>A read whose bytes differ from the preceding read for the same profile and address or register name.</summary>
    ChangedRead,
    /// <summary>A register write without a more specific activity classification.</summary>
    Write,
    /// <summary>An entry whose annotations contain a firmware command code.</summary>
    Command,
    /// <summary>An entry whose annotated meaning is Software Reset.</summary>
    Reset,
    /// <summary>A TP write recognized as selecting a register page.</summary>
    PageSwitch,
    /// <summary>An entry whose register profile resolution is ambiguous.</summary>
    Ambiguous,
}

/// <summary>Stores a source record, its register classification and annotations, and the comparison with a previous read.</summary>
/// <param name="Record">The source record represented by this activity entry.</param>
/// <param name="Kind">The activity classification assigned to the source record.</param>
/// <param name="Profile">The register profile label associated with the entry.</param>
/// <param name="Resolution">The register profile resolution label associated with the entry.</param>
/// <param name="Region">The register region label associated with the entry.</param>
/// <param name="Register">The readable register label or page selection description.</param>
/// <param name="Meaning">The annotated register meaning or page selection label.</param>
/// <param name="RawValue">The annotated register value or hexadecimal representation of the source bytes.</param>
/// <param name="HasPreviousSample">Whether an earlier read exists for the same profile and address or register name.</param>
/// <param name="ChangedFromPreviousSample">Whether this read's bytes differ from those of the previous matching read.</param>
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
    /// <summary>Gets whether the resolution label is resolved, common, or inferred.</summary>
    public bool IsKnown => Resolution is "resolved" or "common" or "inferred";
    /// <summary>Gets whether the resolution label is ambiguous.</summary>
    public bool IsAmbiguous => Resolution == "ambiguous";
}

/// <summary>Projects source records into register activity entries and compares successive reads.</summary>
public static class RegisterActivityProjector
{
    /// <summary>Creates register activity entries using source fields and tracks changes between matching reads.</summary>
    public static IReadOnlyList<RegisterActivityEntry> Project(IEnumerable<SourceRecord> records)
        => Project(records, null);

    /// <summary>Creates register activity entries from annotations or source fields and compares matching reads.</summary>
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
