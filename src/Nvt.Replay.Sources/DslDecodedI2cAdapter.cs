using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed partial class DslDecodedI2cAdapter : ISourceAdapter
{
    private static readonly string[] Header = ["Id", "Time[ns]", "1:I²C: Address/Data"];
    public string Id => "dsl-decoded-i2c";
    public string DisplayName => "DSL decoded I2C CSV";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return None("File extension is not .csv.");
        using var reader = File.OpenText(path);
        var first = await reader.ReadLineAsync(cancellationToken);
        return DelimitedText.HeaderEquals(first, Header)
            ? new(Id, DisplayName, ProbeConfidence.High, ["Matched DSL event-per-row I2C columns exactly."])
            : None("DSL decoded I2C columns were not found.");
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
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_UNSUPPORTED_COLUMNS", "DSL decoded I2C columns do not match the supported schema.", 1);
            yield break;
        }

        var tracker = new NvtRegisterTracker();
        TransactionState? state = null;
        var lineNumber = 1;
        long byteOffset = Encoding.UTF8.GetByteCount(header ?? string.Empty) + Environment.NewLine.Length;
        long outputIndex = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;
            string eventText;
            long eventId;
            double nanoseconds;
            try
            {
                var fields = DelimitedText.ParseCsvLine(line);
                if (fields.Length != Header.Length) throw new FormatException($"expected {Header.Length} columns, got {fields.Length}");
                eventId = long.Parse(fields[0], CultureInfo.InvariantCulture);
                nanoseconds = double.Parse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(nanoseconds)) throw new FormatException("timestamp is not finite");
                eventText = fields[2];
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_MALFORMED_ROW", $"Invalid DSL row {lineNumber}: {exception.Message}", lineNumber, lineOffset);
                state = null;
                continue;
            }

            if (eventText == "Start")
            {
                if (state is not null)
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_NESTED_START", "A new Start arrived before the previous transaction stopped; the prior partial transaction was discarded.", lineNumber, lineOffset);
                }
                state = new TransactionState(eventId, lineNumber, lineOffset, nanoseconds / 1_000_000_000d);
                state.RawLines.Add(line);
                continue;
            }
            if (state is null)
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_EVENT_OUTSIDE_TRANSACTION", $"DSL event '{eventText}' occurred outside Start/Stop.", lineNumber, lineOffset);
                continue;
            }
            state.RawLines.Add(line);
            if (eventText == "Start repeat")
            {
                continue;
            }
            if (eventText == "Stop")
            {
                var record = state.Finish(context, outputIndex, lineNumber, out var error);
                state = null;
                if (record is null)
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_INVALID_TRANSACTION", error, lineNumber, lineOffset);
                    continue;
                }
                outputIndex++;
                yield return tracker.Observe(record);
                continue;
            }

            var match = ValuePattern().Match(eventText);
            if (match.Success)
            {
                state.AddValue(match.Groups["kind"].Value, Convert.ToByte(match.Groups["value"].Value, 16), nanoseconds / 1_000_000_000d);
            }
            else if (eventText is "ACK" or "NACK")
            {
                state.AddAck(eventText == "ACK");
            }
            else if (eventText is not "Read" and not "Write")
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_UNSUPPORTED_EVENT", $"Unsupported DSL I2C event '{eventText}'.", lineNumber, lineOffset);
                state = null;
            }
        }

        if (state is not null)
        {
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_INCOMPLETE_TRANSACTION", "DSL transaction ended without Stop; it was discarded.", state.RowStart, state.ByteOffset);
        }
    }

    [GeneratedRegex(@"^(?<kind>Address (?:read|write)|Data (?:read|write)):\s*(?<value>[0-9A-Fa-f]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();
    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);

    private sealed class TransactionState(long eventId, int rowStart, long byteOffset, double startSeconds)
    {
        private string? awaitingAck;
        private int? readAddress;
        private int? writeAddress;
        private double? readSeconds;
        private bool? addressAcked;
        private readonly List<byte> readData = [];
        private readonly List<byte> writeData = [];
        private readonly List<bool> readAcks = [];

        public int RowStart { get; } = rowStart;
        public long ByteOffset { get; } = byteOffset;
        public List<string> RawLines { get; } = [];

        public void AddValue(string kind, byte value, double seconds)
        {
            switch (kind)
            {
                case "Address read": readAddress = value; readSeconds = seconds; awaitingAck = "address"; break;
                case "Address write": writeAddress = value; awaitingAck = "other"; break;
                case "Data read": readData.Add(value); awaitingAck = "read"; break;
                case "Data write": writeData.Add(value); awaitingAck = "other"; break;
            }
        }

        public void AddAck(bool acked)
        {
            if (awaitingAck == "address") addressAcked = acked;
            if (awaitingAck == "read") readAcks.Add(acked);
            awaitingAck = null;
        }

        public SourceRecord? Finish(SourceOpenContext context, long index, int rowEnd, out string error)
        {
            var operation = readAddress is not null ? BusOperation.Read : writeAddress is not null ? BusOperation.Write : BusOperation.Unknown;
            var slave = readAddress ?? writeAddress;
            var data = operation == BusOperation.Read ? readData.ToArray() : writeData.ToArray();
            if (slave is null || operation == BusOperation.Unknown)
            {
                error = "DSL transaction has no decoded slave address.";
                return null;
            }
            if (operation == BusOperation.Read && readData.Count != readAcks.Count)
            {
                error = $"DSL read data/ACK count mismatch ({readData.Count}/{readAcks.Count}).";
                return null;
            }
            error = string.Empty;
            return new SourceRecord(
                index,
                $"{context.SourceId}:L{RowStart}-L{rowEnd}",
                DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(readSeconds ?? startSeconds),
                operation,
                slave == 1 ? "TP" : $"I2C 0x{slave:X2}",
                null,
                data.Length,
                data,
                string.Join(Environment.NewLine, RawLines),
                new SourceLocation(ByteOffset, RowStart),
                new I2cTransport(
                    slave.Value,
                    operation == BusOperation.Read && writeData.Count > 0 ? [writeData.ToArray()] : [],
                    operation == BusOperation.Read ? readAcks : [],
                    addressAcked),
                new Dictionary<string, string>
                {
                    ["adapter"] = "dsl",
                    ["event_id"] = eventId.ToString(CultureInfo.InvariantCulture),
                    ["row_start"] = RowStart.ToString(CultureInfo.InvariantCulture),
                    ["row_end"] = rowEnd.ToString(CultureInfo.InvariantCulture),
                    ["time_seconds"] = (readSeconds ?? startSeconds).ToString("R", CultureInfo.InvariantCulture),
                });
        }
    }
}
