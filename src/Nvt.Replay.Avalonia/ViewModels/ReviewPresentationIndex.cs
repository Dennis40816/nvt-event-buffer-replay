// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>
/// A get-only presentation snapshot. The workspace transfers freshly materialized collections
/// and never mutates them, retaining the existing collection types and source references.
/// </summary>
internal sealed class ReviewPresentationIndex
{
    internal ReviewPresentationIndex(
        ReviewEventGroup[] visibleGroups,
        Dictionary<string, ReviewEventGroup> visibleGroupsById,
        NavigableOccurrence[] navigableOccurrences,
        Dictionary<string, int> navigablePositionByOccurrenceId)
    {
        VisibleGroups = visibleGroups;
        VisibleGroupsById = visibleGroupsById;
        NavigableOccurrences = navigableOccurrences;
        NavigablePositionByOccurrenceId = navigablePositionByOccurrenceId;
    }

    internal static ReviewPresentationIndex Empty { get; } = new(
        [], new Dictionary<string, ReviewEventGroup>(StringComparer.Ordinal),
        [], new Dictionary<string, int>(StringComparer.Ordinal));

    internal ReviewEventGroup[] VisibleGroups { get; }

    internal IReadOnlyDictionary<string, ReviewEventGroup> VisibleGroupsById { get; }

    internal NavigableOccurrence[] NavigableOccurrences { get; }

    internal IReadOnlyDictionary<string, int> NavigablePositionByOccurrenceId { get; }

    internal int FirstOccurrenceAfter(int logicalIndex)
    {
        var low = 0;
        var high = NavigableOccurrences.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (NavigableOccurrences[middle].Occurrence.LogicalIndex!.Value <= logicalIndex) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    internal int FirstOccurrenceAtOrAfter(int logicalIndex)
    {
        var low = 0;
        var high = NavigableOccurrences.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (NavigableOccurrences[middle].Occurrence.LogicalIndex!.Value < logicalIndex) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}

internal sealed record NavigableOccurrence(
    ReviewEventGroup Group,
    ReviewOccurrence Occurrence,
    int OccurrenceIndex);
