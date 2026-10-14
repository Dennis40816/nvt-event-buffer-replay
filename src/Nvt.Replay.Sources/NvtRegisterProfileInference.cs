using System.Globalization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

/// <summary>Indicates whether register evidence identifies no profile, one profile, multiple profiles, or conflicting profiles.</summary>
public enum NvtRegisterProfileInferenceStatus
{
    /// <summary>No qualifying Event Buffer address evidence was found.</summary>
    None,
    /// <summary>The evidence resolves to exactly one register profile.</summary>
    Unique,
    /// <summary>Multiple register profiles remain compatible with the evidence.</summary>
    Ambiguous,
    /// <summary>The Event Buffer evidence sets have no register profile in common.</summary>
    Conflicting,
}

/// <summary>Identifies whether profile evidence comes from page selection or an absolute register address.</summary>
public enum NvtRegisterProfileEvidenceKind
{
    /// <summary>Evidence pairs a Switch Page command with an Event Buffer access on the selected page.</summary>
    SwitchPage,
    /// <summary>Evidence comes from an absolute NDS register address inside a catalogued Event Buffer.</summary>
    AbsoluteRegister,
}

/// <summary>Records the source location and Event Buffer address used as register profile evidence.</summary>
/// <param name="RecordIndex">Index of the source record containing the evidence.</param>
/// <param name="SourceRecordId">Stable identifier of the source record containing the evidence.</param>
/// <param name="EventBufferAddress">Resolved register address inside a catalogued Event Buffer.</param>
/// <param name="SelectedPage">Event Buffer base address established by page selection or absolute address matching.</param>
/// <param name="SwitchPageCommand">Hexadecimal Switch Page command bytes, or an empty string for absolute register evidence.</param>
/// <param name="Kind">Transport evidence kind used to establish the Event Buffer address.</param>
public sealed record NvtRegisterProfileEvidence(
    long RecordIndex,
    string SourceRecordId,
    uint EventBufferAddress,
    uint SelectedPage,
    string SwitchPageCommand,
    NvtRegisterProfileEvidenceKind Kind = NvtRegisterProfileEvidenceKind.SwitchPage)
{
    /// <summary>Gets display text for the Switch Page command or absolute NDS register address.</summary>
    public string DisplayEvidence => Kind == NvtRegisterProfileEvidenceKind.SwitchPage
        ? $"Switch Page {SwitchPageCommand}"
        : $"NDS absolute register 0x{EventBufferAddress:X}";
}

/// <summary>Holds the profile inference status, candidate profiles, and supporting source evidence.</summary>
/// <param name="Status">Resolution status derived from the collected profile evidence.</param>
/// <param name="Candidates">Compatible profiles, or the combined profiles from conflicting evidence sets.</param>
/// <param name="Evidence">Source records and Event Buffer addresses supporting the inference result.</param>
public sealed record NvtRegisterProfileInferenceResult(
    NvtRegisterProfileInferenceStatus Status,
    IReadOnlyList<NvtRegisterProfile> Candidates,
    IReadOnlyList<NvtRegisterProfileEvidence> Evidence)
{
    /// <summary>Gets the sole candidate when the inference status is unique, or null otherwise.</summary>
    public NvtRegisterProfile? UniqueProfile =>
        Status == NvtRegisterProfileInferenceStatus.Unique && Candidates.Count == 1
            ? Candidates[0]
            : null;

    /// <summary>Gets text summarizing the evidence count, Event Buffer pages, and first record, or reporting no evidence.</summary>
    public string EvidenceSummary
    {
        get
        {
            if (Evidence.Count == 0) return "No complete Switch Page + Event Buffer register evidence was found.";
            var pages = string.Join(", ", Evidence
                .Select(item => $"0x{item.SelectedPage:X5}")
                .Distinct(StringComparer.Ordinal));
            var first = Evidence[0];
            return $"{Evidence.Count:N0} Event Buffer address evidence record(s) for page {pages}; " +
                   $"first evidence at record {first.RecordIndex:N0}: {first.DisplayEvidence}.";
        }
    }
}

/// <summary>
/// Infers an IC profile only from transport evidence that proves both the selected NVT page and
/// an access inside a catalogued Event Buffer. It never rewrites source records and never guesses
/// when two profiles share the same Event Buffer base.
/// </summary>
public static class NvtRegisterProfileInference
{
    private const uint EventBufferLength = 128;
    private const uint HistoryLength = 128;

    /// <summary>Infers profile candidates from Event Buffer address evidence and narrows them using known region accesses.</summary>
    public static NvtRegisterProfileInferenceResult Infer(
        IEnumerable<SourceRecord> records,
        int targetI2cAddress = 0x01)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (targetI2cAddress is < 0 or > 0x7F)
            throw new ArgumentOutOfRangeException(
                nameof(targetI2cAddress),
                targetI2cAddress,
                "I2C slave address must be a 7-bit value from 0x00 through 0x7F.");
        var recordList = records as IReadOnlyList<SourceRecord> ?? records.ToArray();
        var evidence = new List<NvtRegisterProfileEvidence>();
        var candidateSets = new List<HashSet<NvtRegisterProfile>>();

        foreach (var record in recordList)
        {
            if (TryAddAbsoluteNdsEvidence(record, evidence, candidateSets))
            {
                continue;
            }

            if (record.Address is not { } address ||
                record.I2c is not { } i2c ||
                i2c.SlaveAddress != targetI2cAddress)
            {
                continue;
            }

            foreach (var command in i2c.WriteCommands)
            {
                if (!NvtRegisterTracker.TryGetPageSelection(command, out var page)) continue;
                var candidates = NvtRegisterCatalog.Profiles
                    .Where(profile => profile.EventBufferBase == page &&
                                      address >= profile.EventBufferBase &&
                                      address < profile.EventBufferBase + EventBufferLength)
                    .ToHashSet();
                if (candidates.Count == 0) continue;

                evidence.Add(new NvtRegisterProfileEvidence(
                    record.Index,
                    record.StableId,
                    address,
                    page,
                    string.Join(' ', command.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)))));
                candidateSets.Add(candidates);
                break;
            }
        }

        if (candidateSets.Count == 0)
        {
            return new NvtRegisterProfileInferenceResult(
                NvtRegisterProfileInferenceStatus.None,
                [],
                []);
        }

        var intersection = new HashSet<NvtRegisterProfile>(candidateSets[0]);
        foreach (var candidates in candidateSets.Skip(1)) intersection.IntersectWith(candidates);
        if (intersection.Count == 0)
        {
            var conflicting = candidateSets.SelectMany(item => item)
                .Distinct()
                .OrderBy(item => item.IcFamily, StringComparer.Ordinal)
                .ToArray();
            return new NvtRegisterProfileInferenceResult(
                NvtRegisterProfileInferenceStatus.Conflicting,
                conflicting,
                evidence.ToArray());
        }

        // Event Buffer 0x80800 is shared by two catalog profiles. A separately resolved Common
        // Buffer or History access may disambiguate it; otherwise the user must choose explicitly.
        foreach (var record in recordList)
        {
            if (record.Address is not { } address ||
                !BelongsToSelectedTransportOrNds(record, targetI2cAddress))
            {
                continue;
            }
            var matching = intersection.Where(profile => MatchesKnownRegion(profile, address)).ToArray();
            if (matching.Length > 0 && matching.Length < intersection.Count)
                intersection.IntersectWith(matching);
        }

        var resolved = intersection.OrderBy(item => item.IcFamily, StringComparer.Ordinal).ToArray();
        return new NvtRegisterProfileInferenceResult(
            resolved.Length == 1
                ? NvtRegisterProfileInferenceStatus.Unique
                : NvtRegisterProfileInferenceStatus.Ambiguous,
            resolved,
            evidence.ToArray());
    }

    private static bool TryAddAbsoluteNdsEvidence(
        SourceRecord record,
        ICollection<NvtRegisterProfileEvidence> evidence,
        ICollection<HashSet<NvtRegisterProfile>> candidateSets)
    {
        if (record.Address is not { } address || !IsAbsoluteNdsRegister(record)) return false;
        var candidates = NvtRegisterCatalog.Profiles
            .Where(profile => address >= profile.EventBufferBase &&
                              address < profile.EventBufferBase + EventBufferLength)
            .ToHashSet();
        if (candidates.Count == 0) return false;
        var page = candidates.Select(profile => profile.EventBufferBase).Distinct().Single();
        evidence.Add(new NvtRegisterProfileEvidence(
            record.Index,
            record.StableId,
            address,
            page,
            string.Empty,
            NvtRegisterProfileEvidenceKind.AbsoluteRegister));
        candidateSets.Add(candidates);
        return true;
    }

    private static bool BelongsToSelectedTransportOrNds(SourceRecord record, int targetI2cAddress) =>
        record.I2c?.SlaveAddress == targetI2cAddress || IsAbsoluteNdsRegister(record);

    private static bool IsAbsoluteNdsRegister(SourceRecord record) =>
        record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase) &&
        record.SourceFields?.GetValueOrDefault("adapter") == NdsCommunicationLogAdapter.AdapterId &&
        record.SourceFields.GetValueOrDefault(NdsCommunicationLogAdapter.AddressSemanticsField) ==
        NdsCommunicationLogAdapter.AbsoluteRegisterAddress;

    private static bool MatchesKnownRegion(NvtRegisterProfile profile, uint address) =>
        address >= profile.EventBufferBase && address < profile.EventBufferBase + EventBufferLength ||
        address == profile.CommonBufferBase ||
        address >= profile.HistoryBase && address < profile.HistoryBase + HistoryLength;
}
