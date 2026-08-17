namespace Nvt.Replay.Avalonia.ViewModels;

internal static class I2cAckSummary
{
    private const int MaximumListedNakPositions = 8;

    public static string Format(IReadOnlyList<bool> acknowledgements)
    {
        if (acknowledgements.Count == 0) return "-";

        var ackCount = acknowledgements.Count(value => value);
        if (ackCount == acknowledgements.Count) return $"ACK ×{ackCount}";

        var nakPositions = acknowledgements
            .Select((acknowledged, index) => (acknowledged, byteNumber: index + 1))
            .Where(item => !item.acknowledged)
            .Select(item => item.byteNumber)
            .ToArray();

        if (nakPositions.Length == 1 && nakPositions[0] == acknowledgements.Count)
            return $"ACK ×{ackCount}; final NAK";

        var shownPositions = string.Join(", ", nakPositions.Take(MaximumListedNakPositions));
        var hiddenCount = nakPositions.Length - MaximumListedNakPositions;
        var overflow = hiddenCount > 0 ? $" +{hiddenCount} more" : string.Empty;
        var byteLabel = nakPositions.Length == 1 ? "byte" : "bytes";
        return $"ACK ×{ackCount}; NAK ×{nakPositions.Length} at {byteLabel} {shownPositions}{overflow}";
    }
}
