using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class KingstVisDecodedI2cAdapter : ISourceAdapter
{
    private static readonly string[] TransactionHeader = ["Time[s]", "Packet ID", "Address", "Read/Write", "Data"];
    private static readonly string[] LegacyByteHeader = ["Time [s]", "Packet ID", "Address", "Data", "Read/Write", "ACK"];

    public string Id => "kingstvis-decoded-i2c";
    public string DisplayName => "KingstVIS decoded I2C CSV";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return None("File extension is not .csv.");

        using var reader = File.OpenText(path);
        var first = await reader.ReadLineAsync(cancellationToken);
        return DetectDialect(first) switch
        {
            Dialect.TransactionRow => new(Id, DisplayName, ProbeConfidence.High, ["Matched the validated KingstVIS transaction-per-row I2C columns exactly."]),
            Dialect.LegacyByteRow => new(Id, DisplayName, ProbeConfidence.High, ["Matched the legacy KingstVIS byte-per-row I2C columns exactly."]),
            _ => None("Supported KingstVIS decoded-I2C columns were not found."),
        };
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(context.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var layout = TextFileLayout.Detect(context.Path);
        var header = await reader.ReadLineAsync(cancellationToken);
        var dialect = DetectDialect(header);
        if (dialect == Dialect.None)
        {
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_UNSUPPORTED_COLUMNS", "KingstVIS decoded I2C columns do not match a supported schema.", 1);
            yield break;
        }

        var tracker = new NvtRegisterTracker();
        var lineNumber = 1;
        long byteOffset = layout.FirstLineOffset + layout.Advance(header ?? string.Empty);
        long outputIndex = 0;
        long? lastPacketId = null;
        double? lastTimestamp = null;
        if (dialect == Dialect.TransactionRow)
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                var lineOffset = byteOffset;
                byteOffset += layout.Advance(line);
                if (!TryParseTransaction(context, line, lineNumber, lineOffset, outputIndex, out var record, out var error))
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_MALFORMED_ROW", error, lineNumber, lineOffset);
                    tracker.ResetEvidence();
                    continue;
                }
                outputIndex++;
                ValidateSequence(context, record!, ref lastPacketId, ref lastTimestamp);
                yield return tracker.Observe(record!);
            }
            yield break;
        }

        var pending = new List<DecodedByte>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += layout.Advance(line);

            if (!TryParseLegacyByte(line, lineNumber, lineOffset, out var decoded, out var error))
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_MALFORMED_ROW", error, lineNumber, lineOffset);
                pending.Clear();
                tracker.ResetEvidence();
                continue;
            }

            if (pending.Count > 0 && !string.Equals(pending[0].PacketId, decoded!.PacketId, StringComparison.Ordinal))
            {
                var grouped = Finish(context, pending, outputIndex, out var groupError);
                pending.Clear();
                if (grouped is null)
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_INVALID_PACKET", groupError, lineNumber - 1, lineOffset);
                    tracker.ResetEvidence();
                }
                else
                {
                    outputIndex++;
                    ValidateSequence(context, grouped, ref lastPacketId, ref lastTimestamp);
                    yield return tracker.Observe(grouped);
                }
            }
            pending.Add(decoded!);
        }

        if (pending.Count == 0)
            yield break;

        var final = Finish(context, pending, outputIndex, out var finalError);
        if (final is null)
        {
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_INVALID_PACKET", finalError, pending[0].LineNumber, pending[0].ByteOffset);
            tracker.ResetEvidence();
            yield break;
        }
        ValidateSequence(context, final, ref lastPacketId, ref lastTimestamp);
        yield return tracker.Observe(final);
    }

    private static bool TryParseTransaction(
        SourceOpenContext context,
        string line,
        int lineNumber,
        long lineOffset,
        long outputIndex,
        out SourceRecord? record,
        out string error)
    {
        record = null;
        try
        {
            var fields = DelimitedText.ParseCsvLine(line);
            if (fields.Length != TransactionHeader.Length) throw new FormatException($"expected {TransactionHeader.Length} columns, got {fields.Length}");
            var seconds = double.Parse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds)) throw new FormatException("timestamp is not finite");
            var packetId = long.Parse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (packetId < 0) throw new FormatException("packet ID is negative");
            var rawAddress = DelimitedText.ParseInteger(fields[2]);
            if (rawAddress is < 0 or > byte.MaxValue) throw new FormatException("8-bit slave address is outside 0x00..0xFF");
            var operation = ParseOperation(fields[3]);
            if ((rawAddress & 1) != (operation == BusOperation.Read ? 1 : 0))
                throw new FormatException("8-bit slave address R/W bit conflicts with direction");
            var data = DelimitedText.ParseBytes(fields[4]);
            if (data.Length == 0) throw new FormatException("transaction data is empty");
            var slave = rawAddress >> 1;
            record = new SourceRecord(
                outputIndex,
                $"{context.SourceId}:L{lineNumber}",
                DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(seconds),
                operation,
                slave == 1 ? "TP" : $"I2C 0x{slave:X2}",
                null,
                data.Length,
                data,
                line,
                new SourceLocation(lineOffset, lineNumber),
                new I2cTransport(slave, [], [], null),
                new Dictionary<string, string>
                {
                    ["adapter"] = "kingstvis",
                    ["dialect"] = "validated-transaction-row",
                    ["packet_id"] = packetId.ToString(CultureInfo.InvariantCulture),
                    ["raw_address"] = $"0x{rawAddress:X2}",
                    ["row_start"] = lineNumber.ToString(CultureInfo.InvariantCulture),
                    ["row_end"] = lineNumber.ToString(CultureInfo.InvariantCulture),
                    ["time_seconds"] = seconds.ToString("R", CultureInfo.InvariantCulture),
                    ["ack_evidence"] = "unavailable",
                });
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException)
        {
            error = $"Invalid KingstVIS row {lineNumber}: {exception.Message}";
            return false;
        }
    }

    private static bool TryParseLegacyByte(
        string line,
        int lineNumber,
        long lineOffset,
        out DecodedByte? decoded,
        out string error)
    {
        decoded = null;
        try
        {
            var fields = DelimitedText.ParseCsvLine(line);
            if (fields.Length != LegacyByteHeader.Length) throw new FormatException($"expected {LegacyByteHeader.Length} columns, got {fields.Length}");
            var operation = ParseOperation(fields[4]);
            var seconds = double.Parse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds)) throw new FormatException("timestamp is not finite");
            decoded = new DecodedByte(
                seconds,
                fields[1],
                DelimitedText.ParseInteger(fields[2]),
                DelimitedText.ParseByte(fields[3]),
                operation,
                fields[5].ToUpperInvariant() switch
                {
                    "ACK" => true,
                    "NAK" or "NACK" => false,
                    _ => throw new FormatException($"invalid ACK marker '{fields[5]}'"),
                },
                lineNumber,
                lineOffset,
                line);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            error = $"Invalid KingstVIS row {lineNumber}: {exception.Message}";
            return false;
        }
    }

    private static SourceRecord? Finish(
        SourceOpenContext context,
        IReadOnlyList<DecodedByte> rows,
        long outputIndex,
        out string error)
    {
        var rawAddresses = rows.Select(row => row.RawAddress).Distinct().ToArray();
        var operations = rows.Select(row => row.Operation).Distinct().ToArray();
        if (rawAddresses.Length != 1 || operations.Length != 1)
        {
            error = "A KingstVIS packet mixes addresses or read/write directions.";
            return null;
        }

        var operation = operations[0];
        var rawAddress = rawAddresses[0];
        if ((rawAddress & 1) != (operation == BusOperation.Read ? 1 : 0))
        {
            error = "8-bit slave address R/W bit conflicts with direction.";
            return null;
        }

        var slave = rawAddress >> 1;
        var data = rows.Select(row => row.Value).ToArray();
        var raw = string.Join(Environment.NewLine, rows.Select(row => row.RawText));
        error = string.Empty;
        return new SourceRecord(
            outputIndex,
            $"{context.SourceId}:L{rows[0].LineNumber}-L{rows[^1].LineNumber}",
            DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(rows[0].Seconds),
            operation,
            slave == 1 ? "TP" : $"I2C 0x{slave:X2}",
            null,
            data.Length,
            data,
            raw,
            new SourceLocation(rows[0].ByteOffset, rows[0].LineNumber),
            new I2cTransport(slave, [], rows.Select(row => row.Acked).ToArray(), null),
            new Dictionary<string, string>
            {
                ["adapter"] = "kingstvis",
                ["dialect"] = "official-v3.5-byte-row",
                ["packet_id"] = rows[0].PacketId,
                ["raw_address"] = $"0x{rawAddress:X2}",
                ["row_start"] = rows[0].LineNumber.ToString(CultureInfo.InvariantCulture),
                ["row_end"] = rows[^1].LineNumber.ToString(CultureInfo.InvariantCulture),
                ["time_seconds"] = rows[0].Seconds.ToString("R", CultureInfo.InvariantCulture),
            });
    }

    private static BusOperation ParseOperation(string value) => value.ToUpperInvariant() switch
    {
        "R" or "READ" => BusOperation.Read,
        "W" or "WRITE" => BusOperation.Write,
        _ => throw new FormatException($"invalid direction '{value}'"),
    };

    private static void ValidateSequence(
        SourceOpenContext context,
        SourceRecord record,
        ref long? lastPacketId,
        ref double? lastTimestamp)
    {
        var fields = record.SourceFields;
        if (fields is null) return;
        if (fields.TryGetValue("packet_id", out var packetText) &&
            long.TryParse(packetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packetId))
        {
            if (lastPacketId is { } previous && packetId <= previous)
            {
                SourceDiagnostics.Report(
                    context,
                    DiagnosticSeverity.Warning,
                    "KINGSTVIS_PACKET_ID_NON_MONOTONIC",
                    $"Packet ID {packetId} does not follow the previous packet ID {previous}; records were retained in source order.",
                    record.Location.LineNumber,
                    record.Location.ByteOffset);
            }
            lastPacketId = packetId;
        }
        if (fields.TryGetValue("time_seconds", out var timeText) &&
            double.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            if (lastTimestamp is { } previous && seconds < previous)
            {
                SourceDiagnostics.Report(
                    context,
                    DiagnosticSeverity.Warning,
                    "KINGSTVIS_TIMESTAMP_REGRESSION",
                    $"Timestamp {seconds:R}s precedes the prior timestamp {previous:R}s; records were retained in source order.",
                    record.Location.LineNumber,
                    record.Location.ByteOffset);
            }
            lastTimestamp = seconds;
        }
    }

    private static Dialect DetectDialect(string? header)
    {
        if (DelimitedText.HeaderEquals(header, TransactionHeader)) return Dialect.TransactionRow;
        if (DelimitedText.HeaderEquals(header, LegacyByteHeader)) return Dialect.LegacyByteRow;
        return Dialect.None;
    }

    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);

    private enum Dialect
    {
        None,
        TransactionRow,
        LegacyByteRow,
    }

    private sealed record DecodedByte(
        double Seconds,
        string PacketId,
        int RawAddress,
        byte Value,
        BusOperation Operation,
        bool Acked,
        int LineNumber,
        long ByteOffset,
        string RawText);
}
