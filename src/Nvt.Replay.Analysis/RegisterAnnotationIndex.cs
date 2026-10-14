using System.Collections.ObjectModel;
using System.Globalization;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

/// <summary>Associates a source record and register profile identity with a register description.</summary>
/// <param name="SourceRecordId">The stable identifier of the annotated source record.</param>
/// <param name="ProfileIdentity">The register profile identity associated with the annotation.</param>
/// <param name="Description">The register description used to derive the annotation fields.</param>
public sealed record RegisterAnnotation(
    string SourceRecordId,
    NvtRegisterProfileIdentity ProfileIdentity,
    NvtRegisterDescription Description)
{
    /// <summary>Gets the read-only source fields derived from the register description.</summary>
    public IReadOnlyDictionary<string, string> Fields { get; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(Description.ToSourceFields(), StringComparer.Ordinal));
}

/// <summary>Holds register annotations, profile information and diagnostics for a set of source records.</summary>
public sealed class RegisterAnnotationProjection
{
    private readonly IReadOnlyDictionary<string, RegisterAnnotation> annotations;

    internal RegisterAnnotationProjection(
        NvtRegisterProfile? profile,
        NvtRegisterProfileIdentity profileIdentity,
        IDictionary<string, RegisterAnnotation> annotations,
        IReadOnlyList<ReplayDiagnostic> diagnostics)
    {
        Profile = profile;
        ProfileIdentity = profileIdentity;
        this.annotations = new ReadOnlyDictionary<string, RegisterAnnotation>(
            new Dictionary<string, RegisterAnnotation>(annotations, StringComparer.Ordinal));
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the register profile used for this projection, or null for automatic register lookup.</summary>
    public NvtRegisterProfile? Profile { get; }

    /// <summary>Gets the selected profile identity or the automatic profile identity used for this projection.</summary>
    public NvtRegisterProfileIdentity ProfileIdentity { get; }

    /// <summary>Gets the number of source record annotations in this projection.</summary>
    public int Count => annotations.Count;

    /// <summary>Gets the diagnostics produced while observing register activity in source record order.</summary>
    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }

    /// <summary>Finds the annotation for a source record's stable identifier and reports whether it exists.</summary>
    public bool TryGet(SourceRecord record, out RegisterAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(record);
        return annotations.TryGetValue(record.StableId, out annotation!);
    }

    /// <summary>Gets the annotation for a source record identifier, or null if none exists.</summary>
    public RegisterAnnotation? Find(string sourceRecordId) =>
        annotations.GetValueOrDefault(sourceRecordId);

    /// <summary>Builds read-only fields by filtering projected source fields and overlaying any register annotation.</summary>
    public IReadOnlyDictionary<string, string> EffectiveFields(SourceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var offsetOnly = record.SourceFields?.GetValueOrDefault("register_page_known") == "false";
        if (record.SourceFields is not null)
        {
            foreach (var field in record.SourceFields)
            {
                if (!ProjectedFieldKeys.Contains(field.Key) ||
                    offsetOnly && field.Key is "register_name" or "register_address" or "register_offset")
                {
                    result[field.Key] = field.Value;
                }
            }
        }

        if (TryGet(record, out var annotation))
        {
            foreach (var field in annotation.Fields) result[field.Key] = field.Value;
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static readonly IReadOnlySet<string> ProjectedFieldKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "register_profile",
        "register_profile_resolution",
        "register_region",
        "register_name",
        "register_display_name",
        "register_address",
        "register_base",
        "register_offset",
        "register_value",
        "register_meaning",
        "register_readable",
        "fw_command_code",
        "fw_command_name",
        "fw_command_confirmed",
    };
}

/// <summary>Builds and caches register annotation projections by profile identity for source records.</summary>
public sealed class RegisterAnnotationIndex
{
    private readonly object gate = new();
    private readonly IReadOnlyList<SourceRecord> records;
    private readonly Dictionary<NvtRegisterProfileIdentity, RegisterAnnotationProjection> projections = [];

    /// <summary>Creates an annotation index over the supplied source records.</summary>
    public RegisterAnnotationIndex(IReadOnlyList<SourceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        this.records = records;
    }

    /// <summary>Gets or builds a cached projection using the selected profile or automatic register lookup.</summary>
    public RegisterAnnotationProjection Project(
        NvtRegisterProfile? profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = profile?.Identity ?? NvtRegisterCatalog.AutomaticProfileIdentity;
        lock (gate)
        {
            if (projections.TryGetValue(identity, out var cached)) return cached;
        }

        var annotations = new Dictionary<string, RegisterAnnotation>(StringComparer.Ordinal);
        var diagnostics = new List<ReplayDiagnostic>();
        var monitor = new NvtRegisterActivityMonitor();
        for (var index = 0; index < records.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var record = records[index];
            RegisterAnnotation? annotation = null;
            var address = record.Address;
            var inferredFromProfile = false;
            if (address is null &&
                profile is not null &&
                TryResolveOffsetOnlyEventBufferAddress(record, profile, out var inferredAddress))
            {
                address = inferredAddress;
                inferredFromProfile = true;
            }
            if (address is { } resolvedAddress)
            {
                var description = profile is null
                    ? NvtRegisterCatalog.Describe(resolvedAddress, record.Operation, record.Data)
                    : NvtRegisterCatalog.DescribeForProfile(resolvedAddress, record.Operation, record.Data, profile);
                if (description is not null)
                {
                    if (inferredFromProfile)
                        description = description with { ProfileResolution = "inferred" };
                    annotation = new RegisterAnnotation(record.StableId, identity, description);
                    if (!annotations.TryAdd(record.StableId, annotation))
                        throw new InvalidDataException($"Duplicate source record identity '{record.StableId}'.");
                }
            }

            if (monitor.Observe(record, annotation) is { } diagnostic) diagnostics.Add(diagnostic);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var projection = new RegisterAnnotationProjection(profile, identity, annotations, diagnostics.ToArray());
        lock (gate)
        {
            if (projections.TryGetValue(identity, out var cached)) return cached;
            projections.Add(identity, projection);
            return projection;
        }
    }

    private static bool TryResolveOffsetOnlyEventBufferAddress(
        SourceRecord record,
        NvtRegisterProfile profile,
        out uint address)
    {
        address = 0;
        var fields = record.SourceFields;
        if (record.Address is not null ||
            record.I2c is null ||
            !record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase) ||
            fields?.GetValueOrDefault("register_page_known") != "false" ||
            !fields.ContainsKey("register_name") ||
            !fields.TryGetValue("register_offset", out var offsetText))
        {
            return false;
        }

        var hex = offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? offsetText.AsSpan(2)
            : offsetText.AsSpan();
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var offset) || offset >= 128)
            return false;

        address = checked(profile.EventBufferBase + offset);
        return true;
    }
}
