using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Rendering;

/// <summary>
/// Immutable projection of Paint annotations. Diagnostics are grouped once by stable source ID;
/// marker ranges are projected once to logical frames. Scene creation only touches annotations
/// that can be visible in that frame.
/// </summary>
internal sealed class ReplayPaintAnnotationIndex
{
    private static readonly IReadOnlyList<ReplayDiagnostic> NoDiagnostics = Array.Empty<ReplayDiagnostic>();
    private static readonly IReadOnlyList<string> NoMarkerLabels = Array.Empty<string>();

    private readonly IReadOnlyDictionary<string, DiagnosticBucket> diagnosticsBySourceId;
    private readonly IReadOnlyDictionary<int, IReadOnlyList<string>> markerLabelsByLogicalIndex;

    private ReplayPaintAnnotationIndex(
        IReadOnlyDictionary<string, DiagnosticBucket> diagnosticsBySourceId,
        IReadOnlyDictionary<int, IReadOnlyList<string>> markerLabelsByLogicalIndex)
    {
        this.diagnosticsBySourceId = diagnosticsBySourceId;
        this.markerLabelsByLogicalIndex = markerLabelsByLogicalIndex;
    }

    public static ReplayPaintAnnotationIndex Create(
        IReadOnlyList<ReplayDiagnostic> diagnostics,
        IReadOnlyList<ReplayMarker> markers,
        int frameCount)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(markers);
        if (frameCount < 0) throw new ArgumentOutOfRangeException(nameof(frameCount));

        var diagnosticBuilders = new Dictionary<string, List<IndexedDiagnostic>>(StringComparer.Ordinal);
        for (var index = 0; index < diagnostics.Count; index++)
        {
            var diagnostic = diagnostics[index];
            if (!diagnosticBuilders.TryGetValue(diagnostic.SourceRecordId, out var builder))
            {
                builder = [];
                diagnosticBuilders[diagnostic.SourceRecordId] = builder;
            }
            builder.Add(new IndexedDiagnostic(index, diagnostic));
        }
        var diagnosticIndex = diagnosticBuilders.ToDictionary(
            pair => pair.Key,
            pair => new DiagnosticBucket(pair.Value.ToArray()),
            StringComparer.Ordinal);

        var markerBuilders = new Dictionary<int, List<string>>();
        if (frameCount > 0)
        {
            foreach (var marker in markers)
            {
                var start = Math.Max(0, marker.StartLogicalIndex);
                var end = Math.Min(frameCount - 1, marker.EndLogicalIndex);
                if (start > end) continue;
                for (var logicalIndex = start; logicalIndex <= end; logicalIndex++)
                {
                    if (!markerBuilders.TryGetValue(logicalIndex, out var labels))
                    {
                        labels = [];
                        markerBuilders[logicalIndex] = labels;
                    }
                    labels.Add(marker.Label);
                }
            }
        }
        var markerIndex = markerBuilders.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)Array.AsReadOnly(pair.Value.ToArray()));

        return new ReplayPaintAnnotationIndex(diagnosticIndex, markerIndex);
    }

    public ReplayPaintAnnotations Select(ITouchReplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var diagnostics = SelectDiagnostics(snapshot);
        var markerLabels = markerLabelsByLogicalIndex.GetValueOrDefault(snapshot.LogicalIndex, NoMarkerLabels);
        return new ReplayPaintAnnotations(diagnostics, markerLabels);
    }

    private IReadOnlyList<ReplayDiagnostic> SelectDiagnostics(ITouchReplaySnapshot snapshot)
    {
        DiagnosticBucket? first = null;
        List<DiagnosticBucket>? additional = null;
        var physical = snapshot.PhysicalRecords;
        for (var index = 0; index < physical.Count; index++)
        {
            var stableId = physical[index].StableId;
            if (HasEarlierStableId(physical, index, stableId)) continue;
            AddBucket(stableId, ref first, ref additional);
        }

        var primaryId = snapshot.PrimarySource.StableId;
        if (!ContainsStableId(physical, primaryId))
            AddBucket(primaryId, ref first, ref additional);

        if (first is null) return NoDiagnostics;
        if (additional is null) return first.Values;

        var total = first.Indexed.Length;
        foreach (var bucket in additional) total = checked(total + bucket.Indexed.Length);
        var merged = new IndexedDiagnostic[total];
        var destination = 0;
        Copy(first.Indexed, merged, ref destination);
        foreach (var bucket in additional) Copy(bucket.Indexed, merged, ref destination);
        Array.Sort(merged, static (left, right) => left.Order.CompareTo(right.Order));
        var result = new ReplayDiagnostic[merged.Length];
        for (var index = 0; index < merged.Length; index++) result[index] = merged[index].Value;
        return result;
    }

    private void AddBucket(
        string stableId,
        ref DiagnosticBucket? first,
        ref List<DiagnosticBucket>? additional)
    {
        if (!diagnosticsBySourceId.TryGetValue(stableId, out var bucket)) return;
        if (first is null)
        {
            first = bucket;
            return;
        }
        additional ??= [];
        additional.Add(bucket);
    }

    private static void Copy(IndexedDiagnostic[] source, IndexedDiagnostic[] destination, ref int offset)
    {
        source.CopyTo(destination, offset);
        offset += source.Length;
    }

    private static bool HasEarlierStableId(
        IReadOnlyList<SourceRecord> records,
        int exclusiveEnd,
        string stableId)
    {
        for (var index = 0; index < exclusiveEnd; index++)
            if (records[index].StableId.Equals(stableId, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool ContainsStableId(IReadOnlyList<SourceRecord> records, string stableId)
    {
        for (var index = 0; index < records.Count; index++)
            if (records[index].StableId.Equals(stableId, StringComparison.Ordinal)) return true;
        return false;
    }

    private readonly record struct IndexedDiagnostic(int Order, ReplayDiagnostic Value);

    private sealed class DiagnosticBucket
    {
        public DiagnosticBucket(IndexedDiagnostic[] indexed)
        {
            Indexed = indexed;
            var values = new ReplayDiagnostic[indexed.Length];
            for (var index = 0; index < indexed.Length; index++) values[index] = indexed[index].Value;
            Values = values;
        }

        public IndexedDiagnostic[] Indexed { get; }
        public IReadOnlyList<ReplayDiagnostic> Values { get; }
    }
}

internal readonly record struct ReplayPaintAnnotations(
    IReadOnlyList<ReplayDiagnostic> Diagnostics,
    IReadOnlyList<string> MarkerLabels);
