namespace Nvt.Replay.Core;

/// <summary>Identifies the operation represented by a source record.</summary>
public enum BusOperation
{
    /// <summary>Indicates that the source record's operation is unknown.</summary>
    Unknown,
    /// <summary>Identifies an NDS Paint record containing captured event or register data.</summary>
    Paint,
    /// <summary>Identifies a read operation in the capture source.</summary>
    Read,
    /// <summary>Identifies a write operation in the capture source.</summary>
    Write,
}

/// <summary>Identifies a source location by byte offset and line or worksheet row number.</summary>
/// <param name="ByteOffset">The byte offset in the source, or zero when unavailable.</param>
/// <param name="LineNumber">The source line or worksheet row number, or zero for an unspecified location.</param>
public sealed record SourceLocation(long ByteOffset, int LineNumber);

/// <summary>Holds a source record's payload, operation, transport evidence and source location.</summary>
/// <param name="Index">The record's index in the source sequence.</param>
/// <param name="StableId">The stable identifier tying the record to its source.</param>
/// <param name="Timestamp">The source timestamp, or null when unavailable.</param>
/// <param name="Operation">The operation represented by the source record.</param>
/// <param name="Target">The target label recorded by the source or assigned by the adapter.</param>
/// <param name="Address">The resolved register address, or null when unavailable.</param>
/// <param name="DeclaredByteCount">The declared or adapter-reported payload byte count, when available.</param>
/// <param name="Data">The payload bytes in source order.</param>
/// <param name="RawText">The source text associated with the record.</param>
/// <param name="Location">The record's location in the source.</param>
/// <param name="I2c">The I2C transport evidence, when available.</param>
/// <param name="SourceFields">The source-specific fields and adapter annotations, when available.</param>
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

/// <summary>Holds I2C addressing, write commands and acknowledgment evidence for a source record.</summary>
/// <param name="SlaveAddress">The decoded I2C slave address.</param>
/// <param name="WriteCommands">The write-command byte sequences associated with the transaction.</param>
/// <param name="Acked">The payload-byte acknowledgment flags, when recorded.</param>
/// <param name="AddressAcknowledged">Whether the address was acknowledged, or null when unknown.</param>
/// <param name="Error">The transport error text, when available.</param>
public sealed record I2cTransport(
    int SlaveAddress,
    IReadOnlyList<IReadOnlyList<byte>> WriteCommands,
    IReadOnlyList<bool> Acked,
    bool? AddressAcknowledged,
    string? Error = null);

/// <summary>Ranks how confidently a source adapter recognizes a file's grammar.</summary>
public enum ProbeConfidence
{
    /// <summary>Indicates that the source adapter did not recognize the file's grammar.</summary>
    None,
    /// <summary>Indicates a weak match to a source adapter's file grammar.</summary>
    Low,
    /// <summary>Indicates a moderate match to a source adapter's file grammar.</summary>
    Medium,
    /// <summary>Indicates a strong match to a source adapter's file grammar.</summary>
    High,
}

/// <summary>Describes a source adapter's recognition result and the evidence supporting its confidence.</summary>
/// <param name="AdapterId">The identifier of the source adapter that performed the probe.</param>
/// <param name="DisplayName">The display name of the source adapter.</param>
/// <param name="Confidence">The adapter's confidence that the file matches its grammar.</param>
/// <param name="Reasons">The reasons supporting the reported probe confidence.</param>
public sealed record SourceProbeResult(
    string AdapterId,
    string DisplayName,
    ProbeConfidence Confidence,
    IReadOnlyList<string> Reasons);

/// <summary>Supplies the source path, identity and optional diagnostic callback to a source adapter.</summary>
/// <param name="Path">The path of the source file to read.</param>
/// <param name="SourceId">The source identifier used to construct record and diagnostic identities.</param>
/// <param name="DiagnosticSink">The callback that receives source-reading diagnostics, when provided.</param>
public sealed record SourceOpenContext(
    string Path,
    string SourceId,
    Action<ReplayDiagnostic>? DiagnosticSink = null);

/// <summary>Defines how to recognize a capture file's grammar and read its source records.</summary>
public interface ISourceAdapter
{
    /// <summary>Gets the identifier used to select this source adapter.</summary>
    string Id { get; }

    /// <summary>Gets the display name of the capture source adapter.</summary>
    string DisplayName { get; }

    /// <summary>Examines a file and reports the adapter's recognition confidence and reasons.</summary>
    ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Asynchronously reads source records using the supplied source path and diagnostic callback.</summary>
    IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        CancellationToken cancellationToken = default);
}
