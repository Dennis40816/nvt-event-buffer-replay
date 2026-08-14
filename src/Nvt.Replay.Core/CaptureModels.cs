namespace Nvt.Replay.Core;

public enum BusOperation
{
    Unknown,
    Paint,
    Read,
    Write,
}

public sealed record SourceLocation(long ByteOffset, int LineNumber);

public sealed record SourceRecord(
    long Index,
    string StableId,
    DateTimeOffset? Timestamp,
    BusOperation Operation,
    string Target,
    uint? Address,
    int? DeclaredByteCount,
    IReadOnlyList<byte> Data,
    string RawText,
    SourceLocation Location);

public enum ProbeConfidence
{
    None,
    Low,
    Medium,
    High,
}

public sealed record SourceProbeResult(
    string AdapterId,
    string DisplayName,
    ProbeConfidence Confidence,
    IReadOnlyList<string> Reasons);

public sealed record SourceOpenContext(string Path, string SourceId);

public interface ISourceAdapter
{
    string Id { get; }

    string DisplayName { get; }

    ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default);

    IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        CancellationToken cancellationToken = default);
}
