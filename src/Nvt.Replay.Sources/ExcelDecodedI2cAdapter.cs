using System.Globalization;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;
using Nvt.Replay.Core;

namespace Nvt.Replay.Sources;

public sealed class ExcelDecodedI2cAdapter : ISourceAdapter
{
    private static readonly string[] Header = ["Transaction", "Start_Time_s", "Type", "Byte_Count", "Bytes_Hex"];
    public string Id => "excel-decoded-i2c";
    public string DisplayName => "Excel decoded I2C workbook";

    public ValueTask<SourceProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Path.GetExtension(path).ToLowerInvariant() is not (".xlsx" or ".xlsm"))
            return ValueTask.FromResult(None("File extension is not .xlsx or .xlsm."));
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var sharedStrings = ReadSharedStrings(archive);
            var sheet = FindSheet(archive, sharedStrings);
            return ValueTask.FromResult(sheet is not null
                ? new SourceProbeResult(Id, DisplayName, ProbeConfidence.High, ["Matched the Excel transaction table columns in a worksheet."])
                : None("No worksheet contains the supported decoded I2C columns."));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or XmlException)
        {
            return ValueTask.FromResult(None($"Workbook could not be inspected: {exception.Message}"));
        }
    }

    public async IAsyncEnumerable<SourceRecord> ReadAsync(
        SourceOpenContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        using var archive = ZipFile.OpenRead(context.Path);
        var sharedStrings = ReadSharedStrings(archive);
        var sheetName = FindSheet(archive, sharedStrings);
        if (sheetName is null)
        {
            SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "EXCEL_UNSUPPORTED_COLUMNS", "No worksheet contains the supported decoded I2C columns.", 1);
            yield break;
        }

        var entry = archive.GetEntry(sheetName) ?? throw new InvalidDataException($"Worksheet part '{sheetName}' is missing.");
        using var stream = entry.Open();
        var rowNumber = 0;
        await foreach (var row in ReadRowsAsync(stream, sharedStrings, cancellationToken))
        {
            rowNumber++;
            if (rowNumber == 1) continue;
            SourceRecord? record;
            try
            {
                if (row.Length < Header.Length) throw new FormatException($"expected {Header.Length} columns, got {row.Length}");
                var transaction = long.Parse(row[0], CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(row[1])) throw new FormatException("timestamp is missing");
                var seconds = double.Parse(row[1], NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!double.IsFinite(seconds)) throw new FormatException("timestamp is not finite");
                var declared = int.Parse(row[3], CultureInfo.InvariantCulture);
                var raw = Convert.FromHexString(row[4].Replace(" ", string.Empty, StringComparison.Ordinal));
                if (declared != raw.Length) throw new FormatException($"Byte_Count declares {declared}, decoded {raw.Length}");
                if (raw.Length < 2) throw new FormatException("transaction requires an 8-bit slave address and at least one data byte");
                var rawAddress = raw[0];
                var operation = ResolveOperation(row[2], rawAddress);
                var slave = rawAddress >> 1;
                var data = raw[1..];
                var fields = new Dictionary<string, string>
                {
                    ["adapter"] = "excel",
                    ["dialect"] = "legacy-event-buffer-workbook",
                    ["transaction"] = transaction.ToString(CultureInfo.InvariantCulture),
                    ["type"] = row[2],
                    ["byte_count"] = declared.ToString(CultureInfo.InvariantCulture),
                    ["register_address_byte"] = $"0x{rawAddress:X2}",
                    ["register_address"] = "unknown",
                    ["time_seconds"] = seconds.ToString("R", CultureInfo.InvariantCulture),
                };
                if (operation == BusOperation.Read)
                {
                    // This legacy workbook is an Event Buffer extraction, but it carries no
                    // page evidence. Preserve only the offset-level contract; IC profile
                    // selection must resolve an absolute address elsewhere.
                    fields["register_offset"] = "0x00";
                    fields["register_name"] = "event_buffer";
                    fields["register_page_known"] = "false";
                }
                record = new SourceRecord(
                    transaction,
                    $"{context.SourceId}:R{rowNumber}",
                    DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(seconds),
                    operation,
                    slave == 1 ? "TP" : $"I2C 0x{slave:X2}",
                    null,
                    data.Length,
                    data,
                    string.Join(" | ", row.Take(Header.Length)),
                    new SourceLocation(0, rowNumber),
                    new I2cTransport(slave, [], [], null),
                    fields);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                SourceDiagnostics.Report(context, DiagnosticSeverity.Error, "EXCEL_MALFORMED_ROW", $"Invalid Excel decoded I2C row {rowNumber}: {exception.Message}", rowNumber);
                continue;
            }
            yield return record;
        }
    }

    private static string? FindSheet(ZipArchive archive, IReadOnlyList<string> sharedStrings)
    {
        foreach (var entry in archive.Entries.Where(item => item.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            var header = ReadRows(stream, sharedStrings).FirstOrDefault();
            if (header is not null && header.Take(Header.Length).SequenceEqual(Header, StringComparer.Ordinal))
                return entry.FullName;
        }
        return null;
    }

    private static IReadOnlyList<string> ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null) return [];
        using var stream = entry.Open();
        var document = XDocument.Load(stream, LoadOptions.None);
        return document.Descendants().Where(element => element.Name.LocalName == "si")
            .Select(item => string.Concat(item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)))
            .ToArray();
    }

    private static IEnumerable<string[]> ReadRows(Stream stream, IReadOnlyList<string> sharedStrings)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true });
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                var row = (XElement)XNode.ReadFrom(reader);
                yield return DecodeRow(row, sharedStrings);
                continue;
            }
            if (!reader.Read()) break;
        }
    }

    private static async IAsyncEnumerable<string[]> ReadRowsAsync(
        Stream stream,
        IReadOnlyList<string> sharedStrings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings { Async = true, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(stream, settings);
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
            {
                var row = (XElement)await XNode.ReadFromAsync(reader, cancellationToken);
                yield return DecodeRow(row, sharedStrings);
                continue;
            }
            if (!await reader.ReadAsync()) break;
        }
    }

    private static string[] DecodeRow(XElement row, IReadOnlyList<string> sharedStrings)
    {
        var values = new string[Header.Length];
        foreach (var cell in row.Elements().Where(element => element.Name.LocalName == "c"))
        {
            var reference = cell.Attribute("r")?.Value ?? string.Empty;
            var column = ColumnIndex(reference);
            if (column < 0 || column >= values.Length) continue;
            var type = cell.Attribute("t")?.Value;
            var value = cell.Descendants().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ??
                        string.Concat(cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
            values[column] = type == "s" && int.TryParse(value, out var sharedIndex) && sharedIndex >= 0 && sharedIndex < sharedStrings.Count
                ? sharedStrings[sharedIndex]
                : value;
        }
        return values;
    }

    private static int ColumnIndex(string reference)
    {
        var value = 0;
        var found = false;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z') break;
            found = true;
            value = (value * 26) + character - 'A' + 1;
        }
        return found ? value - 1 : -1;
    }

    private static BusOperation ResolveOperation(string type, byte rawAddress)
    {
        var fromAddress = (rawAddress & 1) == 0 ? BusOperation.Write : BusOperation.Read;
        var normalized = type.Trim().ToUpperInvariant();
        var fromType = normalized switch
        {
            "READ" or "R" or "I2C READ" => BusOperation.Read,
            "WRITE" or "W" or "I2C WRITE" => BusOperation.Write,
            _ => fromAddress,
        };
        if (fromType != fromAddress)
            throw new FormatException($"8-bit slave address 0x{rawAddress:X2} conflicts with Type '{type}'");
        return fromType;
    }

    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);
}
