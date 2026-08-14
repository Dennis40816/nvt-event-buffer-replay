using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class KingstVisDecodedI2cAdapter : ISourceAdapter
{
    private static readonly string[] Header = ["Time[s]", "Packet ID", "Address", "Read/Write", "Data"];
    public string Id => "kingstvis-decoded-i2c";
    public string DisplayName => "KingstVIS decoded I2C CSV";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return None("File extension is not .csv.");
        using var reader = File.OpenText(path);
        var first = await reader.ReadLineAsync(cancellationToken);
        return DelimitedText.HeaderEquals(first, Header)
            ? new(Id, DisplayName, ProbeConfidence.High, ["Matched KingstVIS transaction-per-row I2C columns exactly."])
            : None("KingstVIS decoded I2C columns were not found.");
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
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_UNSUPPORTED_COLUMNS", "KingstVIS decoded I2C columns do not match the supported schema.", 1);
            yield break;
        }

        var tracker = new NvtRegisterTracker();
        var lineNumber = 1;
        long byteOffset = Encoding.UTF8.GetByteCount(header ?? string.Empty) + Environment.NewLine.Length;
        long outputIndex = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            SourceRecord? record;
            try
            {
                var fields = DelimitedText.ParseCsvLine(line);
                if (fields.Length != Header.Length) throw new FormatException($"expected {Header.Length} columns, got {fields.Length}");
                var seconds = double.Parse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(seconds)) throw new FormatException("timestamp is not finite");
                var packetId = long.Parse(fields[1], CultureInfo.InvariantCulture);
                var rawAddress = DelimitedText.ParseInteger(fields[2]);
                var operation = fields[3].ToUpperInvariant() switch
                {
                    "R" or "READ" => BusOperation.Read,
                    "W" or "WRITE" => BusOperation.Write,
                    _ => throw new FormatException($"invalid direction '{fields[3]}'"),
                };
                if ((rawAddress & 1) != (operation == BusOperation.Read ? 1 : 0))
                    throw new FormatException("8-bit slave address R/W bit conflicts with direction");
                var slave = rawAddress >> 1;
                var data = DelimitedText.ParseBytes(fields[4]);
                record = new SourceRecord(
                    outputIndex++,
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
                        ["packet_id"] = packetId.ToString(CultureInfo.InvariantCulture),
                        ["raw_address"] = $"0x{rawAddress:X2}",
                        ["time_seconds"] = seconds.ToString("R", CultureInfo.InvariantCulture),
                    });
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "KINGSTVIS_MALFORMED_ROW", $"Invalid KingstVIS row {lineNumber}: {exception.Message}", lineNumber, lineOffset);
                continue;
            }
            yield return tracker.Observe(record);
        }
    }

    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);
}
