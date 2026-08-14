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
    SourceLocation Location,
    I2cTransport? I2c = null,
    IReadOnlyDictionary<string, string>? SourceFields = null);

public sealed record I2cTransport(
    int SlaveAddress,
    IReadOnlyList<IReadOnlyList<byte>> WriteCommands,
    IReadOnlyList<bool> Acked,
    bool? AddressAcknowledged,
    string? Error = null);

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

public sealed record SourceOpenContext(
    string Path,
    string SourceId,
    Action<ReplayDiagnostic>? DiagnosticSink = null);

public interface ISourceAdapter
{
    string Id { get; }

    string DisplayName { get; }

    ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default);

    IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        CancellationToken cancellationToken = default);
}
