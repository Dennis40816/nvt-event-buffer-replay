using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class KingstVisDecodedI2cAdapter : ISourceAdapter
{
    private static readonly string[] Header = ["Time [s]", "Packet ID", "Address", "Data", "Read/Write", "ACK"];

    public string Id => "kingstvis-decoded-i2c";
    public string DisplayName => "KingstVIS decoded I2C CSV";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return None("File extension is not .csv.");

        using var reader = File.OpenText(path);
        var first = await reader.ReadLineAsync(cancellationToken);
        return DelimitedText.HeaderEquals(first, Header)
            ? new(Id, DisplayName, ProbeConfidence.High, ["Matched the official KingstVIS v3.5 byte-per-row I2C columns exactly."])
            : None("Official KingstVIS decoded-I2C columns were not found.");
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
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_UNSUPPORTED_COLUMNS", "KingstVIS decoded I2C columns do not match the official supported schema.", 1);
            yield break;
        }

        var tracker = new NvtRegisterTracker();
        var pending = new List<DecodedByte>();
        var lineNumber = 1;
        long byteOffset = Encoding.UTF8.GetByteCount(header ?? string.Empty) + Environment.NewLine.Length;
        long outputIndex = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            if (!TryParse(line, lineNumber, lineOffset, out var decoded, out var error))
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_MALFORMED_ROW", error, lineNumber, lineOffset);
                pending.Clear();
                continue;
            }

            if (pending.Count > 0 && !string.Equals(pending[0].PacketId, decoded!.PacketId, StringComparison.Ordinal))
            {
                var grouped = Finish(context, pending, outputIndex, out var groupError);
                pending.Clear();
                if (grouped is null)
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_INVALID_PACKET", groupError, lineNumber - 1, lineOffset);
                }
                else
                {
                    outputIndex++;
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
            yield break;
        }
        yield return tracker.Observe(final);
    }

    private static bool TryParse(
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
            if (fields.Length != Header.Length) throw new FormatException($"expected {Header.Length} columns, got {fields.Length}");
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

    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);

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
