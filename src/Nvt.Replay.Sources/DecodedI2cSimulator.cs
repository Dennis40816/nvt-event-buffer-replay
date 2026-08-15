using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Nvt.Replay.Sources;

public sealed record SyntheticI2cTransaction(
    long Index,
    double TimestampSeconds,
    int SlaveAddress,
    IReadOnlyList<IReadOnlyList<byte>> WriteCommands,
    IReadOnlyList<byte> ReadData);

public static class DecodedI2cSimulator
{
    public static string ToSaleaeCsv(IEnumerable<SyntheticI2cTransaction> transactions)
    {
        var lines = new List<string> { "Time [s],Packet ID,Address,Data,Read/Write,ACK/NAK" };
        foreach (var transaction in transactions)
        {
            foreach (var value in transaction.WriteCommands.SelectMany(command => command))
                lines.Add(Row(transaction.TimestampSeconds, transaction.Index, transaction.SlaveAddress, value, "Write", "ACK"));
            for (var index = 0; index < transaction.ReadData.Count; index++)
                lines.Add(Row(transaction.TimestampSeconds, transaction.Index, transaction.SlaveAddress, transaction.ReadData[index], "Read", index == transaction.ReadData.Count - 1 ? "NAK" : "ACK"));
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToKingstVisCsv(IEnumerable<SyntheticI2cTransaction> transactions)
    {
        var lines = new List<string> { "Time [s],Packet ID,Address,Data,Read/Write,ACK" };
        long packetId = 0;
        foreach (var transaction in transactions)
        {
            foreach (var command in transaction.WriteCommands)
            {
                foreach (var value in command)
                    lines.Add($"{Seconds(transaction.TimestampSeconds)},{packetId},0x{transaction.SlaveAddress << 1:X2},0x{value:X2},Write,ACK");
                packetId++;
            }
            for (var index = 0; index < transaction.ReadData.Count; index++)
            {
                var acknowledgement = index == transaction.ReadData.Count - 1 ? "NAK" : "ACK";
                lines.Add($"{Seconds(transaction.TimestampSeconds)},{packetId},0x{(transaction.SlaveAddress << 1) | 1:X2},0x{transaction.ReadData[index]:X2},Read,{acknowledgement}");
            }
            packetId++;
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToDslCsv(IEnumerable<SyntheticI2cTransaction> transactions)
    {
        var lines = new List<string> { "Id,Time[ns],1:I²C: Address/Data" };
        foreach (var transaction in transactions)
        {
            var time = (transaction.TimestampSeconds * 1_000_000_000d).ToString("0", CultureInfo.InvariantCulture);
            void Add(string value) => lines.Add($"{transaction.Index},{time},{value}");
            Add("Start");
            if (transaction.WriteCommands.Count > 0)
            {
                Add($"Address write: {transaction.SlaveAddress:X2}"); Add("ACK");
                foreach (var value in transaction.WriteCommands.SelectMany(command => command)) { Add($"Data write: {value:X2}"); Add("ACK"); }
                Add("Start repeat");
            }
            Add($"Address read: {transaction.SlaveAddress:X2}"); Add("ACK");
            for (var index = 0; index < transaction.ReadData.Count; index++)
            {
                Add($"Data read: {transaction.ReadData[index]:X2}");
                Add(index == transaction.ReadData.Count - 1 ? "NACK" : "ACK");
            }
            Add("Stop");
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToAcuteCsv(IEnumerable<SyntheticI2cTransaction> transactions)
    {
        var materialized = transactions.ToArray();
        var dataColumns = Math.Max(1, materialized
            .SelectMany(transaction => transaction.WriteCommands.Append(transaction.ReadData))
            .Select(data => data.Count)
            .DefaultIfEmpty(1)
            .Max());
        var header = new[] { "Timestamp", "Status", "Address(7b)" }
            .Concat(Enumerable.Range(0, dataColumns).Select(index => $"D{index}"))
            .Concat(["Duration", "Information"]);
        var lines = new List<string> { string.Join(',', header) };
        foreach (var transaction in materialized)
        {
            foreach (var command in transaction.WriteCommands)
                lines.Add(AcuteRow(transaction.TimestampSeconds, "ACK", $"Wr {transaction.SlaveAddress:X2}", command, dataColumns, false));
            lines.Add(AcuteRow(transaction.TimestampSeconds, "ACK", $"Rd {transaction.SlaveAddress:X2}", transaction.ReadData, dataColumns, true));
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static string ToCanonicalText(IEnumerable<SyntheticI2cTransaction> transactions)
    {
        var lines = new List<string> { CanonicalI2cTextAdapter.Magic };
        foreach (var transaction in transactions)
        {
            lines.Add(JsonSerializer.Serialize(new
            {
                index = transaction.Index,
                timestamp_s = transaction.TimestampSeconds,
                operation = "Read",
                i2c_slave_address = transaction.SlaveAddress,
                write_commands = transaction.WriteCommands.Select(Bytes).ToArray(),
                read_data = Bytes(transaction.ReadData),
                acks = transaction.ReadData.Select((_, index) => index != transaction.ReadData.Count - 1).ToArray(),
                source_meta = new { generator = "Nvt.Replay.Sources", schema = 1 },
            }));
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static void WriteExcel(string path, IEnumerable<SyntheticI2cTransaction> transactions)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
            """);
        WriteEntry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="I2C" sheetId="1" r:id="rId1"/></sheets></workbook>
            """);
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
            """);

        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        AppendRow(xml, 1, ["Transaction", "Start_Time_s", "Type", "Byte_Count", "Bytes_Hex"]);
        var row = 2;
        foreach (var transaction in transactions)
        {
            var bytes = new byte[] { 0x03 }.Concat(transaction.ReadData).ToArray();
            AppendRow(xml, row++, [transaction.Index.ToString(CultureInfo.InvariantCulture), Seconds(transaction.TimestampSeconds), "I2C", bytes.Length.ToString(CultureInfo.InvariantCulture), Bytes(bytes)]);
        }
        xml.Append("</sheetData></worksheet>");
        WriteEntry(archive, "xl/worksheets/sheet1.xml", xml.ToString());
    }

    private static string Row(double seconds, long packet, int address, byte value, string direction, string ack) =>
        $"{Seconds(seconds)},{packet},0x{address:X2},0x{value:X2},{direction},{ack}";

    private static string AcuteRow(
        double seconds,
        string status,
        string address,
        IReadOnlyList<byte> data,
        int dataColumns,
        bool finalNack)
    {
        var fields = new List<string> { Seconds(seconds), status, address };
        fields.AddRange(Enumerable.Range(0, dataColumns).Select(index =>
            index < data.Count
                ? $"{data[index]:X2}{(finalNack && index == data.Count - 1 ? " NACK" : " ACK")}"
                : string.Empty));
        fields.Add(string.Empty);
        fields.Add(string.Empty);
        return string.Join(',', fields);
    }

    private static string Seconds(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Bytes(IEnumerable<byte> values) => string.Join(' ', values.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }

    private static void AppendRow(StringBuilder xml, int row, IReadOnlyList<string> values)
    {
        xml.Append("<row r=\"").Append(row).Append("\">");
        for (var column = 0; column < values.Count; column++)
            xml.Append("<c r=\"").Append((char)('A' + column)).Append(row).Append("\" t=\"inlineStr\"><is><t>").Append(System.Security.SecurityElement.Escape(values[column])).Append("</t></is></c>");
        xml.Append("</row>");
    }
}
