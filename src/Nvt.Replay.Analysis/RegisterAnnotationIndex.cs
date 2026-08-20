using System.Collections.ObjectModel;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

public sealed record RegisterAnnotation(
    string SourceRecordId,
    NvtRegisterProfileIdentity ProfileIdentity,
    NvtRegisterDescription Description);

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

    public NvtRegisterProfile? Profile { get; }

    public NvtRegisterProfileIdentity ProfileIdentity { get; }

    public int Count => annotations.Count;

    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }

    public bool TryGet(SourceRecord record, out RegisterAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(record);
        return annotations.TryGetValue(record.StableId, out annotation!);
    }

    public RegisterAnnotation? Find(string sourceRecordId) =>
        annotations.GetValueOrDefault(sourceRecordId);
}

public sealed class RegisterAnnotationIndex
{
    private readonly object gate = new();
    private readonly IReadOnlyList<SourceRecord> records;
    private readonly Dictionary<NvtRegisterProfileIdentity, RegisterAnnotationProjection> projections = [];

    public RegisterAnnotationIndex(IReadOnlyList<SourceRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        this.records = records;
    }

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
            if (record.Address is { } address)
            {
                var description = profile is null
                    ? NvtRegisterCatalog.Describe(address, record.Operation, record.Data)
                    : NvtRegisterCatalog.DescribeForProfile(address, record.Operation, record.Data, profile);
                if (description is not null)
                {
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
}
