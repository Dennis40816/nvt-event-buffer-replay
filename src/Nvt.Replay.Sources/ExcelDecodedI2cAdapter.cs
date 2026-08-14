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
                var isRead = raw.Length >= 3;
                if (isRead && raw[0] != 0x03) throw new FormatException($"expected read-address byte 0x03, got 0x{raw[0]:X2}");
                var data = isRead ? raw[1..] : raw;
                record = new SourceRecord(
                    transaction,
                    $"{context.SourceId}:R{rowNumber}",
                    DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(seconds),
                    isRead ? BusOperation.Read : BusOperation.Write,
                    "TP",
                    isRead ? 0x99000u : null,
                    data.Length,
                    data,
                    string.Join(" | ", row.Take(Header.Length)),
                    new SourceLocation(0, rowNumber),
                    new I2cTransport(1, [], [], null),
                    new Dictionary<string, string>
                    {
                        ["adapter"] = "excel",
                        ["transaction"] = transaction.ToString(CultureInfo.InvariantCulture),
                        ["type"] = row[2],
                        ["byte_count"] = declared.ToString(CultureInfo.InvariantCulture),
                        ["register_address_byte"] = isRead ? "0x03" : "none",
                        ["register_address"] = isRead ? "0x99000" : "unknown",
                        ["time_seconds"] = seconds.ToString("R", CultureInfo.InvariantCulture),
                    });
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

    private SourceProbeResult None(string reason) => new(Id, DisplayName, ProbeConfidence.None, [reason]);
}
