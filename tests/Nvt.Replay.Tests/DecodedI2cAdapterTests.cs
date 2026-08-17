using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Sources;
using System.Text;

namespace Nvt.Replay.Tests;

public sealed class DecodedI2cAdapterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-i2c-{Guid.NewGuid():N}");
    private static readonly SyntheticI2cTransaction Transaction = new(
        7,
        1.25,
        1,
        [new byte[] { 0xFF, 0x09, 0x90, 0x00 }],
        new byte[] { 0x01, 0x23, 0x45, 0x67 });

    public DecodedI2cAdapterTests() => Directory.CreateDirectory(directory);

    public static IEnumerable<object[]> TextAdapters()
    {
        yield return [new SaleaeDecodedI2cAdapter(), ".csv", (Func<string>)(() => DecodedI2cSimulator.ToSaleaeCsv([Transaction]))];
        yield return [new KingstVisDecodedI2cAdapter(), ".csv", (Func<string>)(() => DecodedI2cSimulator.ToKingstVisCsv([Transaction]))];
        yield return [new DslDecodedI2cAdapter(), ".csv", (Func<string>)(() => DecodedI2cSimulator.ToDslCsv([Transaction]))];
        yield return [new AcuteDecodedI2cAdapter(), ".csv", (Func<string>)(() => DecodedI2cSimulator.ToAcuteCsv([Transaction]))];
        yield return [new CanonicalI2cTextAdapter(), ".txt", (Func<string>)(() => DecodedI2cSimulator.ToCanonicalText([Transaction]))];
    }

    [Theory]
    [MemberData(nameof(TextAdapters))]
    public async Task Generated_text_exports_probe_and_normalize_equivalently(
        ISourceAdapter adapter,
        string extension,
        Func<string> generate)
    {
        var path = Write(extension, generate());

        var probe = await adapter.ProbeAsync(path);
        var records = await Read(adapter, path);
        var read = Assert.Single(records, record => record.Operation == BusOperation.Read);

        Assert.Equal(ProbeConfidence.High, probe.Confidence);
        Assert.Equal(1, read.I2c?.SlaveAddress);
        Assert.Equal(0x99000u, read.Address);
        Assert.Equal(Transaction.ReadData, read.Data);
        Assert.NotNull(read.Timestamp);
        Assert.NotEmpty(read.RawText);
        Assert.True(read.SourceFields?.ContainsKey("adapter"));
    }

    [Fact]
    public async Task Generated_excel_export_is_streamed_without_Excel_and_normalizes_to_event_buffer()
    {
        var path = Path.Combine(directory, "capture.XLSX");
        DecodedI2cSimulator.WriteExcel(path, [Transaction]);
        var adapter = new ExcelDecodedI2cAdapter();

        var probe = await adapter.ProbeAsync(path);
        var read = Assert.Single(await Read(adapter, path));

        Assert.Equal(ProbeConfidence.High, probe.Confidence);
        Assert.Equal(BusOperation.Read, read.Operation);
        Assert.Null(read.Address);
        Assert.Equal(Transaction.ReadData, read.Data);
        Assert.Equal("0x03", read.SourceFields?["register_address_byte"]);
        Assert.Equal("0x00", read.SourceFields?["register_offset"]);
        Assert.Equal("false", read.SourceFields?["register_page_known"]);
        Assert.Equal("unknown", read.SourceFields?["register_address"]);
    }

    [Fact]
    public async Task Excel_malformed_timestamp_is_diagnosed_and_cancel_is_observed()
    {
        var path = Path.Combine(directory, "malformed.xlsx");
        DecodedI2cSimulator.WriteExcel(path, [Transaction with { TimestampSeconds = double.NaN }]);
        var adapter = new ExcelDecodedI2cAdapter();
        var diagnostics = new List<ReplayDiagnostic>();

        Assert.Empty(await Read(adapter, path, diagnostics.Add));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "EXCEL_MALFORMED_ROW");

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in adapter.ReadAsync(new SourceOpenContext(path, "cancelled"), cancellation.Token)) { }
        });
    }

    [Fact]
    public async Task Capture_session_selects_each_unique_source_without_inferring_event_format()
    {
        var path = Write(".csv", DecodedI2cSimulator.ToKingstVisCsv([Transaction]));

        var session = await CaptureSession.LoadAsync(path);

        Assert.Equal("kingstvis-decoded-i2c", session.Probe.AdapterId);
        Assert.Equal(2, session.Records.Count);
        Assert.Empty(session.SourceDiagnostics);
    }

    [Fact]
    public async Task KingstVis_validated_transaction_rows_preserve_each_complete_payload()
    {
        var path = Write(".csv", DecodedI2cSimulator.ToKingstVisCsv([Transaction]));
        var adapter = new KingstVisDecodedI2cAdapter();

        var records = await Read(adapter, path);
        var read = Assert.Single(records, record => record.Operation == BusOperation.Read);

        Assert.Equal(2, records.Count);
        Assert.Equal("validated-transaction-row", read.SourceFields?["dialect"]);
        Assert.Equal(Transaction.ReadData, read.Data);
        Assert.Empty(read.I2c?.Acked ?? []);
        Assert.Equal("unavailable", read.SourceFields?["ack_evidence"]);
    }

    [Fact]
    public async Task Excel_normalizes_the_8bit_address_without_assuming_the_TP_slave()
    {
        var transaction = Transaction with { SlaveAddress = 0x2A };
        var path = Path.Combine(directory, "other-slave.xlsx");
        DecodedI2cSimulator.WriteExcel(path, [transaction]);

        var read = Assert.Single(await Read(new ExcelDecodedI2cAdapter(), path));

        Assert.Equal(0x2A, read.I2c?.SlaveAddress);
        Assert.Equal("0x55", read.SourceFields?["register_address_byte"]);
        Assert.Equal("I2C 0x2A", read.Target);
    }

    [Fact]
    public async Task KingstVis_transaction_row_validates_8bit_address_direction()
    {
        var path = Write(".csv", "Time[s],Packet ID,Address,Read/Write,Data\n1,1,0x02,Read,0x00 0x01\n");
        var diagnostics = new List<ReplayDiagnostic>();
        var adapter = new KingstVisDecodedI2cAdapter();

        var probe = await adapter.ProbeAsync(path);
        var records = await Read(adapter, path, diagnostics.Add);

        Assert.Equal(ProbeConfidence.High, probe.Confidence);
        Assert.Empty(records);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KINGSTVIS_MALFORMED_ROW");
    }

    [Fact]
    public async Task KingstVis_non_monotonic_packet_and_timestamp_are_retained_with_warnings()
    {
        var path = Write(".csv", string.Join('\n',
            "Time[s],Packet ID,Address,Read/Write,Data",
            "2,2,0x03,Read,0x01",
            "1.5,1,0x03,Read,0x02") + "\n");
        var diagnostics = new List<ReplayDiagnostic>();

        var records = await Read(new KingstVisDecodedI2cAdapter(), path, diagnostics.Add);

        Assert.Equal(2, records.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KINGSTVIS_PACKET_ID_NON_MONOTONIC");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "KINGSTVIS_TIMESTAMP_REGRESSION");
    }

    [Fact]
    public async Task KingstVis_legacy_byte_rows_remain_grouped_by_packet_id()
    {
        var path = Write(".csv", string.Join('\n',
            "Time [s],Packet ID,Address,Data,Read/Write,ACK",
            "1,7,0x02,0xFF,Write,ACK",
            "1,7,0x02,0x09,Write,ACK",
            "1,7,0x02,0x90,Write,ACK",
            "1,7,0x02,0x00,Write,ACK",
            "1.1,8,0x03,0x01,Read,ACK",
            "1.1,8,0x03,0x02,Read,NAK") + "\n");
        var adapter = new KingstVisDecodedI2cAdapter();

        var probe = await adapter.ProbeAsync(path);
        var records = await Read(adapter, path);
        var read = Assert.Single(records, record => record.Operation == BusOperation.Read);

        Assert.Equal(ProbeConfidence.High, probe.Confidence);
        Assert.Equal(2, records.Count);
        Assert.Equal("official-v3.5-byte-row", read.SourceFields?["dialect"]);
        Assert.Equal([true, false], read.I2c?.Acked);
        Assert.Equal(0x99000u, read.Address);
    }

    [Fact]
    public async Task KingstVis_three_byte_page_command_and_separate_offset_resolve_the_real_capture_shape()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        var transaction = Transaction with
        {
            WriteCommands = [new byte[] { 0xFF, 0x08, 0x08 }, new byte[] { 0x00 }],
            ReadData = packet,
        };
        var path = Write(".csv", DecodedI2cSimulator.ToKingstVisCsv([transaction]));

        var session = (await CaptureSession.LoadAsync(path)).WithRegisterProfile("51929/51932");
        var read = Assert.Single(session.Records, record => record.Operation == BusOperation.Read);
        var report = session.DecodeCommon(CommonEventBufferVersion.V83);

        Assert.Equal(0x80800u, read.Address);
        Assert.Equal(2, read.I2c?.WriteCommands.Count);
        Assert.True(Assert.Single(report.Frames).CrcValid);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "NO_EVENT_BUFFER_RECORDS");
    }

    [Fact]
    public async Task Dsl_event_buffer_reads_preserve_and_decode_trailing_bytes()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V84);
        var trailing = new byte[] { 0xA5, 0x5A };
        var transaction = Transaction with
        {
            WriteCommands = [new byte[] { 0xFF, 0x08, 0x08, 0x00 }],
            ReadData = packet.Concat(trailing).ToArray(),
        };
        var path = Write(".csv", DecodedI2cSimulator.ToDslCsv([transaction]));

        var session = (await CaptureSession.LoadAsync(path)).WithRegisterProfile("51929/51932");
        var read = Assert.Single(session.Records, record => record.Operation == BusOperation.Read);
        Assert.Equal(0x80800u, read.Address);
        Assert.Equal("0x00", read.SourceFields?["register_offset"]);
        Assert.Equal("Event Buffer", read.SourceFields?["register_region"]);
        Assert.Equal(packet.Length + trailing.Length, read.Data.Count);

        var report = session.DecodeCommon(CommonEventBufferVersion.V84);
        var frame = Assert.Single(report.Frames);
        Assert.True(frame.CrcValid);
        Assert.Equal(trailing, frame.UnconsumedData);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "NO_EVENT_BUFFER_RECORDS");
    }

    [Fact]
    public async Task Dsl_discovers_the_single_I2c_analyzer_and_ignores_unrelated_signal_columns()
    {
        var rows = DecodedI2cSimulator.ToDslCsv([Transaction])
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(row =>
            {
                var fields = row.Split(',', 3);
                return $"{fields[0]},{fields[1]},ignored,{fields[2]},0";
            });
        var text = "Id,Time[ns],0:SPI: MOSI,2:I2C: Address/Data,3:Digital: D0\n" + string.Join('\n', rows) + "\n";
        var path = Write(".csv", text);
        var adapter = new DslDecodedI2cAdapter();

        var probe = await adapter.ProbeAsync(path);
        var read = Assert.Single(await Read(adapter, path));

        Assert.Equal(ProbeConfidence.High, probe.Confidence);
        Assert.Equal("2:I2C: Address/Data", read.SourceFields?["analyzer_column"]);
        Assert.Equal(Transaction.ReadData, read.Data);
    }

    [Fact]
    public async Task Dsl_source_location_uses_real_UTF16_BOM_and_line_width()
    {
        var text = DecodedI2cSimulator.ToDslCsv([Transaction]);
        var path = Path.Combine(directory, "utf16-dsl.csv");
        File.WriteAllText(path, text, Encoding.Unicode);
        var header = text.Split(["\r\n", "\n"], StringSplitOptions.None)[0];
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var expected = Encoding.Unicode.GetPreamble().Length + Encoding.Unicode.GetByteCount(header + newline);

        var read = Assert.Single(await Read(new DslDecodedI2cAdapter(), path));

        Assert.Equal(expected, read.Location.ByteOffset);
        Assert.Equal(2, read.Location.LineNumber);
    }

    [Theory]
    [InlineData("saleae")]
    [InlineData("dsl")]
    public async Task Repeated_start_write_segments_round_trip_without_flattening(string format)
    {
        var transaction = Transaction with
        {
            WriteCommands = [new byte[] { 0xFF, 0x08, 0x08 }, new byte[] { 0x00 }],
        };
        var (adapter, extension, text) = format switch
        {
            "saleae" => ((ISourceAdapter)new SaleaeDecodedI2cAdapter(), ".csv", DecodedI2cSimulator.ToSaleaeCsv([transaction])),
            _ => (new DslDecodedI2cAdapter(), ".csv", DecodedI2cSimulator.ToDslCsv([transaction])),
        };
        var path = Write(extension, text);

        var read = Assert.Single(await Read(adapter, path), record => record.Operation == BusOperation.Read);

        Assert.Equal(2, read.I2c?.WriteCommands.Count);
        Assert.Equal(new byte[] { 0xFF, 0x08, 0x08 }, read.I2c?.WriteCommands[0]);
        Assert.Equal(new byte[] { 0x00 }, read.I2c?.WriteCommands[1]);
        Assert.Equal(0x80800u, read.Address);
    }

    [Fact]
    public async Task Dsl_write_to_read_without_repeated_start_is_rejected()
    {
        var text = DecodedI2cSimulator.ToDslCsv([Transaction])
            .Replace("7,1250000000,Start repeat\r\n", string.Empty, StringComparison.Ordinal)
            .Replace("7,1250000000,Start repeat\n", string.Empty, StringComparison.Ordinal);
        var path = Write(".csv", text);
        var diagnostics = new List<ReplayDiagnostic>();

        var records = await Read(new DslDecodedI2cAdapter(), path, diagnostics.Add);

        Assert.Empty(records);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "DSL_INVALID_TRANSACTION");
    }

    [Fact]
    public void Built_in_sources_include_every_supported_decoded_LA_path()
    {
        var ids = BuiltInSources.All.Select(adapter => adapter.Id).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("saleae-decoded-i2c", ids);
        Assert.Contains("kingstvis-decoded-i2c", ids);
        Assert.Contains("dsl-decoded-i2c", ids);
        Assert.Contains("acute-decoded-i2c", ids);
    }

    [Fact]
    public async Task Acute_semicolon_time_of_day_and_8bit_address_are_normalized_without_guessing()
    {
        var path = Write(".txt", string.Join('\n',
            "Timestamp;Status;Address(8b);D0;D1;D2;D3;Duration;Information",
            "12:00:00.000;ACK;Wr 02;FF;09;90;00;;page",
            "12:00:00.010;ACK;Rd 03;01;02;03 NACK;;;read") + "\n");
        var adapter = new AcuteDecodedI2cAdapter();

        var probe = await adapter.ProbeAsync(path);
        var records = await Read(adapter, path);
        var read = Assert.Single(records, record => record.Operation == BusOperation.Read);

        Assert.Equal(ProbeConfidence.High, probe.Confidence);
        Assert.Equal(1, read.I2c?.SlaveAddress);
        Assert.Equal(0x99000u, read.Address);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, read.Data);
        Assert.Equal("true", read.SourceFields?["nack"]);
        Assert.Equal("8b", read.SourceFields?["address_mode"]);
    }

    [Fact]
    public async Task KingstVis_offset_only_read_decodes_the_real_capture_shape_without_inventing_a_page()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        var transaction = Transaction with
        {
            WriteCommands = [new byte[] { 0x00 }],
            ReadData = packet,
        };
        var path = Write(".csv", DecodedI2cSimulator.ToKingstVisCsv([transaction]));

        var session = await CaptureSession.LoadAsync(path);
        var read = Assert.Single(session.Records, record => record.Operation == BusOperation.Read);
        var report = session.DecodeCommon(CommonEventBufferVersion.V83);

        Assert.Null(read.Address);
        Assert.Equal("0x00", read.SourceFields?["register_offset"]);
        Assert.Equal("event_buffer", read.SourceFields?["register_name"]);
        Assert.Equal("false", read.SourceFields?["register_page_known"]);
        Assert.True(Assert.Single(report.Frames).CrcValid);
        Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Code == "NO_EVENT_BUFFER_RECORDS");
    }

    [Theory]
    [MemberData(nameof(TextAdapters))]
    public async Task Cancellation_is_observed_before_records_are_committed(
        ISourceAdapter adapter,
        string extension,
        Func<string> generate)
    {
        var path = Write(extension, generate());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in adapter.ReadAsync(new SourceOpenContext(path, "cancelled"), cancellation.Token)) { }
        });
    }

    [Fact]
    public async Task Malformed_and_partial_rows_are_diagnostics_not_silent_records()
    {
        var cases = new (ISourceAdapter Adapter, string Extension, string Text, string Code)[]
        {
            (new SaleaeDecodedI2cAdapter(), ".csv", "Time [s],Packet ID,Address,Data,Read/Write,ACK/NAK\nnot-a-time,1,0x01,0x00,Read,NAK\n", "SALEAE_MALFORMED_ROW"),
            (new KingstVisDecodedI2cAdapter(), ".csv", "Time [s],Packet ID,Address,Data,Read/Write,ACK\n1,1,0x03,00,Write,ACK\n", "KINGSTVIS_INVALID_PACKET"),
            (new DslDecodedI2cAdapter(), ".csv", "Id,Time[ns],1:I²C: Address/Data\n1,0,Start\n1,0,Address read: 01\n", "DSL_INCOMPLETE_TRANSACTION"),
            (new AcuteDecodedI2cAdapter(), ".csv", "Timestamp,Status,Address(7b),D0\n0,ACK,Rd 01,not-a-byte\n", "ACUTE_MALFORMED_ROW"),
            (new CanonicalI2cTextAdapter(), ".txt", CanonicalI2cTextAdapter.Magic + "\n{broken}\n", "CANONICAL_MALFORMED_RECORD"),
        };

        foreach (var item in cases)
        {
            var path = Write(item.Extension, item.Text);
            var diagnostics = new List<ReplayDiagnostic>();
            var records = await Read(item.Adapter, path, diagnostics.Add);
            Assert.Empty(records);
            Assert.Contains(diagnostics, diagnostic => diagnostic.Code == item.Code);
        }
    }

    [Fact]
    public async Task Missing_canonical_timestamp_is_retained_with_warning()
    {
        var path = Write(".txt", CanonicalI2cTextAdapter.Magic + "\n" +
            "{\"index\":1,\"operation\":\"Read\",\"read_data\":\"00\",\"i2c_slave_address\":1}\n");
        var diagnostics = new List<ReplayDiagnostic>();

        var record = Assert.Single(await Read(new CanonicalI2cTextAdapter(), path, diagnostics.Add));

        Assert.Null(record.Timestamp);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Code == "CANONICAL_TIMESTAMP_MISSING");
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Write(string extension, string contents)
    {
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, contents);
        return path;
    }

    private static async Task<List<SourceRecord>> Read(
        ISourceAdapter adapter,
        string path,
        Action<ReplayDiagnostic>? diagnostics = null)
    {
        var records = new List<SourceRecord>();
        await foreach (var record in adapter.ReadAsync(new SourceOpenContext(path, "fixture", diagnostics)))
            records.Add(record);
        return records;
    }
}
