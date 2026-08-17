using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed partial class AcuteDecodedI2cAdapter : ISourceAdapter
{
    public string Id => "acute-decoded-i2c";
    public string DisplayName => "Acute decoded I2C report";

    public async ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            return None("File extension is not .csv or .txt.");

        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        for (var inspected = 0; inspected < 20 && await reader.ReadLineAsync(cancellationToken) is { } line; inspected++)
        {
            foreach (var delimiter in CandidateDelimiters(line, extension))
            {
                if (TryColumns(DelimitedText.ParseLine(line, delimiter), out _))
                    return new(Id, DisplayName, ProbeConfidence.High,
                        [$"Matched Acute row-per-transaction columns using '{DelimiterName(delimiter)}' delimiter."]);
            }
        }
        return None("Acute Timestamp/Status/Address/D0 columns were not found in the first 20 lines.");
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(context.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var extension = Path.GetExtension(context.Path);
        var tracker = new NvtRegisterTracker();
        AcuteColumns? columns = null;
        char delimiter = ',';
        long byteOffset = 0;
        long outputIndex = 0;
        var lineNumber = 0;
        double? firstTimestamp = null;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lineNumber++;
            var lineOffset = byteOffset;
            byteOffset += Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length;

            if (columns is null)
            {
                foreach (var candidate in CandidateDelimiters(line, extension))
                {
                    if (!TryColumns(DelimitedText.ParseLine(line, candidate), out var parsed)) continue;
                    columns = parsed;
                    delimiter = candidate;
                    break;
                }
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;
            SourceRecord? record;
            try
            {
                var fields = DelimitedText.ParseLine(line, delimiter);
                var addressText = Cell(fields, columns.Address);
                var dataCells = columns.Data.Select(index => Cell(fields, index)).ToArray();
                if (string.IsNullOrWhiteSpace(addressText))
                {
                    if (dataCells.Any(value => !string.IsNullOrWhiteSpace(value)))
                        throw new FormatException("data continuation without an address is not supported until validated against a real Acute export");
                    continue;
                }

                var (operation, rawAddress) = ParseAddress(addressText);
                var slave = NormalizeAddress(rawAddress, operation, columns.AddressMode);
                var (data, hasNack) = ParseData(dataCells);
                if (data.Length == 0) throw new FormatException("transaction has no data bytes");
                var rawTimestamp = Cell(fields, columns.Timestamp);
                var timestamp = ParseTimestamp(rawTimestamp);
                firstTimestamp ??= timestamp;
                var relative = timestamp - firstTimestamp.Value;
                if (relative < 0) relative += TimeSpan.FromDays(1).TotalSeconds;
                if (!double.IsFinite(relative)) throw new FormatException("timestamp is not finite");

                var acked = data.Select((_, index) => !hasNack || index != data.Length - 1).ToArray();
                record = new SourceRecord(
                    outputIndex++,
                    $"{context.SourceId}:L{lineNumber}",
                    DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(relative),
                    operation,
                    slave == 1 ? "TP" : $"I2C 0x{slave:X2}",
                    null,
                    data.Length,
                    data,
                    line,
                    new SourceLocation(lineOffset, lineNumber),
                    new I2cTransport(slave, [], acked, null),
                    new Dictionary<string, string>
                    {
                        ["adapter"] = "acute",
                        ["address_mode"] = columns.AddressMode,
                        ["raw_address"] = $"0x{rawAddress:X}",
                        ["status"] = Cell(fields, columns.Status),
                        ["raw_timestamp"] = rawTimestamp,
                        ["duration"] = OptionalCell(fields, columns.Duration) ?? string.Empty,
                        ["information"] = OptionalCell(fields, columns.Information) ?? string.Empty,
                        ["nack"] = hasNack.ToString().ToLowerInvariant(),
                    });
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                SourceDiagnostics.Report(
                    context,
                    DiagnosticSeverity.Error,
                    "ACUTE_MALFORMED_ROW",
                    $"Invalid Acute row {lineNumber}: {exception.Message}",
                    lineNumber,
                    lineOffset);
                continue;
            }
            yield return tracker.Observe(record);
        }

        if (columns is null)
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "ACUTE_UNSUPPORTED_COLUMNS", "No supported Acute I2C report header was found.", Math.Max(1, lineNumber));
    }

    private static bool TryColumns(string[] fields, out AcuteColumns columns)
    {
        var normalized = fields.Select(value => value.TrimStart('\uFEFF').Trim()).ToArray();
        var timestamp = Array.FindIndex(normalized, value => value.StartsWith("Timestamp", StringComparison.OrdinalIgnoreCase));
        var status = Array.FindIndex(normalized, value => value.Equals("Status", StringComparison.OrdinalIgnoreCase));
        var address = Array.FindIndex(normalized, value => AddressHeaderRegex().IsMatch(value));
        var data = normalized
            .Select((value, index) => (value, index, match: DataHeaderRegex().Match(value)))
            .Where(item => item.match.Success)
            .OrderBy(item => int.Parse(item.match.Groups[1].Value, CultureInfo.InvariantCulture))
            .Select(item => item.index)
            .ToArray();
        if (timestamp < 0 || status < 0 || address < 0 || data.Length == 0)
        {
            columns = default!;
            return false;
        }
        var modeMatch = AddressHeaderRegex().Match(normalized[address]);
        columns = new AcuteColumns(
            timestamp,
            status,
            address,
            (modeMatch.Groups[1].Value.ToLowerInvariant()) switch { "8b" => "8b", "10b" => "10b", _ => "7b" },
            data,
            Array.FindIndex(normalized, value => value.Equals("Duration", StringComparison.OrdinalIgnoreCase)),
            Array.FindIndex(normalized, value => value.Equals("Information", StringComparison.OrdinalIgnoreCase)));
        return true;
    }

    private static (BusOperation Operation, int RawAddress) ParseAddress(string value)
    {
        var match = AddressValueRegex().Match(value.Trim());
        if (!match.Success) throw new FormatException($"invalid address value '{value}'");
        var operation = match.Groups[1].Value.ToLowerInvariant() is "rd" or "read" ? BusOperation.Read : BusOperation.Write;
        return (operation, int.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static int NormalizeAddress(int rawAddress, BusOperation operation, string mode)
    {
        var limit = mode switch { "8b" => 0xff, "10b" => 0x3ff, _ => 0x7f };
        if (rawAddress < 0 || rawAddress > limit) throw new FormatException($"Address({mode}) value is out of range: 0x{rawAddress:X}");
        if (mode != "8b") return rawAddress;
        var expectedBit = operation == BusOperation.Read ? 1 : 0;
        if ((rawAddress & 1) != expectedBit) throw new FormatException("8-bit address R/W bit conflicts with the direction marker");
        return rawAddress >> 1;
    }

    private static (byte[] Data, bool HasNack) ParseData(IEnumerable<string> cells)
    {
        var values = new List<byte>();
        var hasNack = false;
        foreach (var cell in cells.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            hasNack |= cell.Contains("NACK", StringComparison.OrdinalIgnoreCase);
            var matches = DataByteRegex().Matches(AckRegex().Replace(cell, " "));
            if (matches.Count == 0) throw new FormatException($"invalid data field '{cell}'");
            values.AddRange(matches.Select(match => byte.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }
        return (values.ToArray(), hasNack);
    }

    private static double ParseTimestamp(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return seconds;
        var match = TimeOfDayRegex().Match(value);
        if (!match.Success) throw new FormatException($"unsupported timestamp '{value}'");
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600) +
               (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60) +
               double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private static IEnumerable<char> CandidateDelimiters(string line, string extension) =>
        new[] { ',', '\t', ';' }
            .OrderByDescending(delimiter => line.Count(character => character == delimiter))
            .ThenBy(delimiter => delimiter == (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ? ',' : '\t') ? 0 : 1);

    private static string Cell(IReadOnlyList<string> fields, int index) => index >= 0 && index < fields.Count ? fields[index].Trim() : string.Empty;
    private static string? OptionalCell(IReadOnlyList<string> fields, int index) => index < 0 ? null : Cell(fields, index);
    private static string DelimiterName(char delimiter) => delimiter == '\t' ? "tab" : delimiter.ToString();
    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);

    private sealed record AcuteColumns(int Timestamp, int Status, int Address, string AddressMode, int[] Data, int Duration, int Information);

    [GeneratedRegex(@"^Address(?:\((7b|8b|10b)\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressHeaderRegex();
    [GeneratedRegex(@"^D(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DataHeaderRegex();
    [GeneratedRegex(@"^(Wr|Rd|Write|Read)\s+(?:0x)?([0-9a-f]{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex AddressValueRegex();
    [GeneratedRegex(@"\bN?ACK\b|0x", RegexOptions.IgnoreCase)]
    private static partial Regex AckRegex();
    [GeneratedRegex(@"(?<![0-9a-f])([0-9a-f]{2})(?![0-9a-f])", RegexOptions.IgnoreCase)]
    private static partial Regex DataByteRegex();
    [GeneratedRegex(@"(\d{1,2}):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex TimeOfDayRegex();
}
