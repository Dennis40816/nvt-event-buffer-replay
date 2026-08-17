using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed partial class DslDecodedI2cAdapter : ISourceAdapter
{
    public string Id => "dsl-decoded-i2c";
    public string DisplayName => "DSL decoded I2C CSV";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            return None("File extension is not .csv.");
        using var reader = File.OpenText(path);
        var first = await reader.ReadLineAsync(cancellationToken);
        return TryResolveSchema(first, out _, out _, out var analyzerColumn, out var reason)
            ? new(Id, DisplayName, ProbeConfidence.High, [$"Matched DSL decoded-I2C analyzer column '{analyzerColumn}'; unrelated signal columns will be ignored."])
            : None(reason);
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(context.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var layout = TextFileLayout.Detect(context.Path);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (!TryResolveSchema(header, out var eventColumn, out var columnCount, out var analyzerColumn, out var schemaError))
        {
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_UNSUPPORTED_COLUMNS", schemaError, 1);
            yield break;
        }

        var tracker = new NvtRegisterTracker();
        TransactionState? state = null;
        var lineNumber = 1;
        long byteOffset = layout.FirstLineOffset + layout.Advance(header ?? string.Empty);
        long outputIndex = 0;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += layout.Advance(line);
            string eventText;
            long eventId;
            double nanoseconds;
            try
            {
                var fields = DelimitedText.ParseCsvLine(line);
                if (fields.Length != columnCount) throw new FormatException($"expected {columnCount} columns, got {fields.Length}");
                eventId = long.Parse(fields[0], CultureInfo.InvariantCulture);
                nanoseconds = double.Parse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(nanoseconds)) throw new FormatException("timestamp is not finite");
                eventText = fields[eventColumn];
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_MALFORMED_ROW", $"Invalid DSL row {lineNumber}: {exception.Message}", lineNumber, lineOffset);
                state = null;
                tracker.ResetEvidence();
                continue;
            }

            if (eventText == "Start")
            {
                if (state is not null)
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_NESTED_START", "A new Start arrived before the previous transaction stopped; the prior partial transaction was discarded.", lineNumber, lineOffset);
                    tracker.ResetEvidence();
                }
                state = new TransactionState(eventId, lineNumber, lineOffset, nanoseconds / 1_000_000_000d, analyzerColumn);
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
                state.BeginRepeatedStart();
                continue;
            }
            if (eventText == "Stop")
            {
                var record = state.Finish(context, outputIndex, lineNumber, out var error);
                state = null;
                if (record is null)
                {
                    SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_INVALID_TRANSACTION", error, lineNumber, lineOffset);
                    tracker.ResetEvidence();
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
                tracker.ResetEvidence();
            }
        }

        if (state is not null)
        {
            tracker.ResetEvidence();
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "DSL_INCOMPLETE_TRANSACTION", "DSL transaction ended without Stop; it was discarded.", state.RowStart, state.ByteOffset);
        }
    }

    [GeneratedRegex(@"^(?<kind>Address (?:read|write)|Data (?:read|write)):\s*(?<value>[0-9A-Fa-f]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern();
    [GeneratedRegex(@"^\d+:I(?:²|2)C:\s*Address/Data$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnalyzerColumnPattern();
    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);

    private static bool TryResolveSchema(
        string? header,
        out int eventColumn,
        out int columnCount,
        out string analyzerColumn,
        out string error)
    {
        eventColumn = -1;
        columnCount = 0;
        analyzerColumn = string.Empty;
        error = "DSL decoded I2C columns were not found.";
        if (header is null) return false;
        string[] fields;
        try
        {
            fields = DelimitedText.ParseCsvLine(header);
        }
        catch (FormatException exception)
        {
            error = $"DSL header is malformed: {exception.Message}";
            return false;
        }
        columnCount = fields.Length;
        if (fields.Length < 3 || fields[0] != "Id" || fields[1] != "Time[ns]")
        {
            error = "DSL header must begin with Id,Time[ns].";
            return false;
        }
        var candidates = fields
            .Select((value, index) => (value, index))
            .Where(item => AnalyzerColumnPattern().IsMatch(item.value))
            .ToArray();
        if (candidates.Length != 1)
        {
            error = candidates.Length == 0
                ? "DSL decoded I2C analyzer column was not found."
                : $"DSL contains {candidates.Length} decoded I2C analyzer columns; export one analyzer or select it explicitly before import.";
            return false;
        }
        eventColumn = candidates[0].index;
        analyzerColumn = candidates[0].value;
        error = string.Empty;
        return true;
    }

    private sealed class TransactionState(long eventId, int rowStart, long byteOffset, double startSeconds, string analyzerColumn)
    {
        private string? awaitingAck;
        private int? readAddress;
        private int? writeAddress;
        private double? readSeconds;
        private bool? addressAcked;
        private readonly List<byte> readData = [];
        private readonly List<IReadOnlyList<byte>> writeCommands = [];
        private List<byte>? currentWriteCommand;
        private readonly List<bool> readAcks = [];
        private string? protocolError;

        public int RowStart { get; } = rowStart;
        public long ByteOffset { get; } = byteOffset;
        public List<string> RawLines { get; } = [];

        public void BeginRepeatedStart()
        {
            CompleteWriteCommand();
            awaitingAck = null;
        }

        public void AddValue(string kind, byte value, double seconds)
        {
            switch (kind)
            {
                case "Address read":
                    if (currentWriteCommand is not null) protocolError ??= "DSL changed from write to read without a repeated Start.";
                    CompleteWriteCommand();
                    if (writeAddress is not null && writeAddress != value) protocolError ??= "DSL repeated Start changed the slave address.";
                    readAddress = value;
                    readSeconds = seconds;
                    awaitingAck = "address";
                    break;
                case "Address write":
                    if (currentWriteCommand is not null) protocolError ??= "DSL started another write segment without a repeated Start.";
                    if (writeAddress is not null && writeAddress != value) protocolError ??= "DSL repeated Start changed the slave address.";
                    writeAddress = value;
                    currentWriteCommand = [];
                    awaitingAck = "other";
                    break;
                case "Data read": readData.Add(value); awaitingAck = "read"; break;
                case "Data write":
                    if (currentWriteCommand is null) protocolError ??= "DSL write data arrived without an Address write segment.";
                    else currentWriteCommand.Add(value);
                    awaitingAck = "other";
                    break;
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
            CompleteWriteCommand();
            var operation = readAddress is not null ? BusOperation.Read : writeAddress is not null ? BusOperation.Write : BusOperation.Unknown;
            var slave = readAddress ?? writeAddress;
            var data = operation == BusOperation.Read ? readData.ToArray() : writeCommands.SelectMany(command => command).ToArray();
            if (protocolError is not null)
            {
                error = protocolError;
                return null;
            }
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
                    operation == BusOperation.Read ? writeCommands : [],
                    operation == BusOperation.Read ? readAcks : [],
                    addressAcked),
                new Dictionary<string, string>
                {
                    ["adapter"] = "dsl",
                    ["event_id"] = eventId.ToString(CultureInfo.InvariantCulture),
                    ["row_start"] = RowStart.ToString(CultureInfo.InvariantCulture),
                    ["row_end"] = rowEnd.ToString(CultureInfo.InvariantCulture),
                    ["time_seconds"] = (readSeconds ?? startSeconds).ToString("R", CultureInfo.InvariantCulture),
                    ["analyzer_column"] = analyzerColumn,
                });
        }

        private void CompleteWriteCommand()
        {
            if (currentWriteCommand is null) return;
            if (currentWriteCommand.Count == 0) protocolError ??= "DSL write segment contains no data bytes.";
            else writeCommands.Add(currentWriteCommand.ToArray());
            currentWriteCommand = null;
        }
    }
}
