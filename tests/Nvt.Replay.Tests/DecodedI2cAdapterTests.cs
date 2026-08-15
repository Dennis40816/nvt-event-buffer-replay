using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Sources;

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
        Assert.Equal(0x99000u, read.Address);
        Assert.Equal(Transaction.ReadData, read.Data);
        Assert.Equal("0x03", read.SourceFields?["register_address_byte"]);
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
            (new KingstVisDecodedI2cAdapter(), ".csv", "Time[s],Packet ID,Address,Read/Write,Data\n1,1,0x03,W,00\n", "KINGSTVIS_MALFORMED_ROW"),
            (new DslDecodedI2cAdapter(), ".csv", "Id,Time[ns],1:I²C: Address/Data\n1,0,Start\n1,0,Address read: 01\n", "DSL_INCOMPLETE_TRANSACTION"),
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
