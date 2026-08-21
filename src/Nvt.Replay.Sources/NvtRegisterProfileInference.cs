using System.Globalization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public enum NvtRegisterProfileInferenceStatus
{
    None,
    Unique,
    Ambiguous,
    Conflicting,
}

public sealed record NvtRegisterProfileEvidence(
    long RecordIndex,
    string SourceRecordId,
    uint EventBufferAddress,
    uint SelectedPage,
    string SwitchPageCommand);

public sealed record NvtRegisterProfileInferenceResult(
    NvtRegisterProfileInferenceStatus Status,
    IReadOnlyList<NvtRegisterProfile> Candidates,
    IReadOnlyList<NvtRegisterProfileEvidence> Evidence)
{
    public NvtRegisterProfile? UniqueProfile =>
        Status == NvtRegisterProfileInferenceStatus.Unique && Candidates.Count == 1
            ? Candidates[0]
            : null;

    public string EvidenceSummary
    {
        get
        {
            if (Evidence.Count == 0) return "No complete Switch Page + Event Buffer register evidence was found.";
            var pages = string.Join(", ", Evidence
                .Select(item => $"0x{item.SelectedPage:X5}")
                .Distinct(StringComparer.Ordinal));
            var first = Evidence[0];
            return $"{Evidence.Count:N0} Event Buffer access(es) after Switch Page {pages}; " +
                   $"first evidence at record {first.RecordIndex:N0}: {first.SwitchPageCommand}.";
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
                record.I2c?.SlaveAddress != targetI2cAddress)
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

    private static bool MatchesKnownRegion(NvtRegisterProfile profile, uint address) =>
        address >= profile.EventBufferBase && address < profile.EventBufferBase + EventBufferLength ||
        address == profile.CommonBufferBase ||
        address >= profile.HistoryBase && address < profile.HistoryBase + HistoryLength;
}
