using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class SaleaeDecodedI2cAdapter : ISourceAdapter
{
    private static readonly string[] Header = ["Time [s]", "Packet ID", "Address", "Data", "Read/Write", "ACK/NAK"];

    public string Id => "saleae-decoded-i2c";
    public string DisplayName => "Saleae decoded I2C CSV";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return None("File extension is not .csv.");
        }
        using var reader = File.OpenText(path);
        var first = await reader.ReadLineAsync(cancellationToken);
        return DelimitedText.HeaderEquals(first, Header)
            ? new(Id, DisplayName, ProbeConfidence.High, ["Matched Saleae byte-per-row I2C columns exactly."])
            : None("Saleae byte-per-row I2C columns were not found.");
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(context.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (!DelimitedText.HeaderEquals(header, Header))
        {
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "SALEAE_UNSUPPORTED_COLUMNS", "Saleae decoded I2C columns do not match the supported schema.", 1);
            yield break;
        }

        var pending = new List<DecodedByte>();
        var lineNumber = 1;
        long byteOffset = Encoding.UTF8.GetByteCount(header ?? string.Empty) + Environment.NewLine.Length;
        long outputIndex = 0;
        var tracker = new NvtRegisterTracker();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            if (!TryParse(line, lineNumber, lineOffset, out var decoded, out var error))
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "SALEAE_MALFORMED_ROW", error, lineNumber, lineOffset);
                pending.Clear();
                continue;
            }
            pending.Add(decoded!);
            if (decoded!.Acked)
            {
                continue;
            }

            var record = Finish(context, pending, outputIndex, out error);
            pending.Clear();
            if (record is null)
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "SALEAE_INVALID_TRANSACTION", error, lineNumber, lineOffset);
                continue;
            }
            outputIndex++;
            yield return tracker.Observe(record);
        }

        if (pending.Count > 0)
        {
            var first = pending[0];
            SourceDiagnostics.Report(
                context,
                DiagnosticSeverity.Error,
                "SALEAE_INCOMPLETE_TRANSACTION",
                "Saleae transaction ended without a terminating NAK; it was discarded.",
                first.LineNumber,
                first.ByteOffset);
        }
    }

    private static SourceRecord? Finish(
        SourceOpenContext context,
        IReadOnlyList<DecodedByte> rows,
        long outputIndex,
        out string error)
    {
        var addresses = rows.Select(row => row.Address).Distinct().ToArray();
        if (addresses.Length != 1)
        {
            error = "Saleae transaction contains multiple slave addresses.";
            return null;
        }
        var reads = rows.Where(row => row.Operation == BusOperation.Read).ToArray();
        var writes = rows.Where(row => row.Operation == BusOperation.Write).ToArray();
        var payloadRows = reads.Length > 0 ? reads : writes;
        if (payloadRows.Length == 0)
        {
            error = "Saleae transaction contains no data bytes.";
            return null;
        }

        var operation = reads.Length > 0 ? BusOperation.Read : BusOperation.Write;
        var raw = string.Join(Environment.NewLine, rows.Select(row => row.RawText));
        var writeCommands = reads.Length > 0 && writes.Length > 0
            ? new IReadOnlyList<byte>[] { writes.Select(row => row.Value).ToArray() }
            : [];
        error = string.Empty;
        return new SourceRecord(
            outputIndex,
            $"{context.SourceId}:L{rows[0].LineNumber}-L{rows[^1].LineNumber}",
            RelativeTime(rows[0].Seconds),
            operation,
            addresses[0] == 0x01 ? "TP" : $"I2C 0x{addresses[0]:X2}",
            null,
            payloadRows.Length,
            payloadRows.Select(row => row.Value).ToArray(),
            raw,
            new SourceLocation(rows[0].ByteOffset, rows[0].LineNumber),
            new I2cTransport(
                addresses[0],
                writeCommands,
                payloadRows.Select(row => row.Acked).ToArray(),
                null),
            new Dictionary<string, string>
            {
                ["adapter"] = "saleae",
                ["row_start"] = rows[0].LineNumber.ToString(CultureInfo.InvariantCulture),
                ["row_end"] = rows[^1].LineNumber.ToString(CultureInfo.InvariantCulture),
                ["packet_ids"] = string.Join(',', rows.Select(row => row.PacketId).Distinct()),
                ["time_seconds"] = rows[0].Seconds.ToString("R", CultureInfo.InvariantCulture),
            });
    }

    private static bool TryParse(
        string line,
        int lineNumber,
        long byteOffset,
        out DecodedByte? row,
        out string error)
    {
        row = null;
        try
        {
            var fields = DelimitedText.ParseCsvLine(line);
            if (fields.Length != Header.Length) throw new FormatException($"expected {Header.Length} columns, got {fields.Length}");
            var seconds = double.Parse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture);
            if (!double.IsFinite(seconds)) throw new FormatException("timestamp is not finite");
            var address = DelimitedText.ParseInteger(fields[2]);
            var value = DelimitedText.ParseByte(fields[3]);
            var operation = fields[4].ToLowerInvariant() switch
            {
                "read" => BusOperation.Read,
                "write" => BusOperation.Write,
                _ => throw new FormatException($"invalid direction '{fields[4]}'"),
            };
            var acked = fields[5].ToUpperInvariant() switch
            {
                "ACK" => true,
                "NAK" or "NACK" => false,
                _ => throw new FormatException($"invalid ACK marker '{fields[5]}'"),
            };
            row = new DecodedByte(seconds, fields[1], address, value, operation, acked, lineNumber, byteOffset, line);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            error = $"Invalid Saleae row {lineNumber}: {exception.Message}";
            return false;
        }
    }

    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);
    private static DateTimeOffset RelativeTime(double seconds) => DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(seconds);

    private sealed record DecodedByte(
        double Seconds,
        string PacketId,
        int Address,
        byte Value,
        BusOperation Operation,
        bool Acked,
        int LineNumber,
        long ByteOffset,
        string RawText);
}
