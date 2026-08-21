using System.Text;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class NdsCommunicationLogAdapterTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"nvt-replay-{Guid.NewGuid():N}");

    public NdsCommunicationLogAdapterTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
    }

    [Fact]
    public async Task Probe_recognizes_NDS_read_write_grammar()
    {
        var path = WriteCapture(
            """
            2026-07-29 14:52:41:241 Read TP 0x99060 1 0xA3
            2026-07-29 14:52:41:243 Write TP 0x99050 2 0x00 0xBB
            2026-07-29 14:52:41:267 Read TP 0x99051 3 0xBB 0x01 0x00
            """);
        var adapter = new NdsCommunicationLogAdapter();

        var result = await adapter.ProbeAsync(path);

        Assert.Equal(ProbeConfidence.High, result.Confidence);
    }

    [Fact]
    public async Task Reader_joins_continuation_bytes_and_preserves_source_line()
    {
        var path = WriteCapture(
            """
            2026-07-29 14:52:41:405 Read TP 0x8EC98 6 0x00 0x01
            0x02 0x03 0xFE 0xFF
            """);
        var adapter = new NdsCommunicationLogAdapter();

        var records = new List<SourceRecord>();
        await foreach (var record in adapter.ReadAsync(new SourceOpenContext(path, "capture-a")))
        {
            records.Add(record);
        }

        var parsed = Assert.Single(records);
        Assert.Equal(BusOperation.Read, parsed.Operation);
        Assert.Equal(0x8EC98u, parsed.Address);
        Assert.Equal([0x00, 0x01, 0x02, 0x03, 0xFE, 0xFF], parsed.Data);
        Assert.Equal(1, parsed.Location.LineNumber);
        Assert.Equal("capture-a:L1", parsed.StableId);
    }

    [Fact]
    public async Task Reader_does_not_mistake_two_digit_address_for_payload()
    {
        var path = WriteCapture("2026-07-29 14:52:41:241 Read TP 0x50 1 0xA3");
        var adapter = new NdsCommunicationLogAdapter();

        SourceRecord? parsed = null;
        await foreach (var record in adapter.ReadAsync(new SourceOpenContext(path, "capture-b")))
        {
            parsed = record;
        }

        Assert.NotNull(parsed);
        Assert.Equal([0xA3], parsed.Data);
        Assert.Equal(0x50u, parsed.Address);
        Assert.Null(parsed.I2c);
        Assert.Equal(NdsCommunicationLogAdapter.AbsoluteRegisterAddress, parsed.SourceFields?[NdsCommunicationLogAdapter.AddressSemanticsField]);
        Assert.Equal(NdsCommunicationLogAdapter.RegisterRead, parsed.SourceFields?[NdsCommunicationLogAdapter.AccessSemanticsField]);
    }

    [Fact]
    public async Task Reader_keeps_small_Read_Write_tokens_as_absolute_registers_while_Paint_is_an_event_buffer_read()
    {
        var path = WriteCapture(
            """
            2026-03-18 15:21:18:346 Write TP 0x62 2 0x00 0x35
            2026-03-18 15:21:18:347 Read TP 0x62 1 0xA3
            2026-03-18 15:21:18:348 Paint TP 0x02 2 0x05 0x00
            """);
        var records = new List<SourceRecord>();
        await foreach (var record in new NdsCommunicationLogAdapter().ReadAsync(new SourceOpenContext(path, "operation-semantics")))
            records.Add(record);

        Assert.Equal(3, records.Count);
        Assert.Equal(0x62u, records[0].Address);
        Assert.Equal(NdsCommunicationLogAdapter.RegisterWrite, records[0].SourceFields?[NdsCommunicationLogAdapter.AccessSemanticsField]);
        Assert.Equal(0x62u, records[1].Address);
        Assert.Equal(NdsCommunicationLogAdapter.RegisterRead, records[1].SourceFields?[NdsCommunicationLogAdapter.AccessSemanticsField]);
        Assert.Null(records[2].Address);
        Assert.Equal(0x02, records[2].I2c?.SlaveAddress);
        Assert.Equal(NdsCommunicationLogAdapter.EventBufferRead, records[2].SourceFields?[NdsCommunicationLogAdapter.AccessSemanticsField]);
    }

    [Fact]
    public async Task Reader_supports_NDS_Paint_and_keeps_extra_payload_evidence()
    {
        var path = WriteCapture(
            """
            2026-07-29 14:52:41:241 Paint TP 0x01 2 0xA3 0x55
              0x66
            """);
        var adapter = new NdsCommunicationLogAdapter();

        var records = new List<SourceRecord>();
        await foreach (var record in adapter.ReadAsync(new SourceOpenContext(path, "paint")))
        {
            records.Add(record);
        }

        var parsed = Assert.Single(records);
        Assert.Equal(BusOperation.Paint, parsed.Operation);
        Assert.Equal(2, parsed.DeclaredByteCount);
        Assert.Equal([0xA3, 0x55, 0x66], parsed.Data);
        Assert.Null(parsed.Address);
        Assert.Equal(0x01, parsed.I2c?.SlaveAddress);
        Assert.Equal(NdsCommunicationLogAdapter.I2cSlaveAddress, parsed.SourceFields?[NdsCommunicationLogAdapter.AddressSemanticsField]);
        Assert.Equal(NdsCommunicationLogAdapter.EventBufferRead, parsed.SourceFields?[NdsCommunicationLogAdapter.AccessSemanticsField]);
    }

    [Fact]
    public async Task Paint_header_distinguishes_7bit_I2C_address_from_absolute_register_address()
    {
        var path = WriteCapture(
            """
            2026-08-12 19:21:52:824 Paint TP 0x99000 2 0x50 0x00
            2026-08-12 19:21:54:003 Paint TP 0x2A 2 0xA3 0x55
            """);
        var records = new List<SourceRecord>();
        await foreach (var record in new NdsCommunicationLogAdapter().ReadAsync(new SourceOpenContext(path, "mixed")))
            records.Add(record);

        Assert.Equal(2, records.Count);
        Assert.Equal(0x99000u, records[0].Address);
        Assert.Null(records[0].I2c);
        Assert.Equal(NdsCommunicationLogAdapter.AbsoluteRegisterAddress, records[0].SourceFields?[NdsCommunicationLogAdapter.AddressSemanticsField]);
        Assert.Equal(NdsCommunicationLogAdapter.RegisterRead, records[0].SourceFields?[NdsCommunicationLogAdapter.AccessSemanticsField]);
        Assert.Null(records[1].Address);
        Assert.Equal(0x2A, records[1].I2c?.SlaveAddress);
        Assert.Equal("0x2A", records[1].SourceFields?["raw_address"]);
        Assert.Equal(NdsCommunicationLogAdapter.EventBufferRead, records[1].SourceFields?[NdsCommunicationLogAdapter.AccessSemanticsField]);
    }

    [Fact]
    public async Task Paint_header_rejects_the_ambiguous_non_7bit_range()
    {
        var path = WriteCapture("2026-08-12 19:21:54:003 Paint TP 0x80 2 0xA3 0x55");
        var diagnostics = new List<ReplayDiagnostic>();
        var records = new List<SourceRecord>();

        await foreach (var record in new NdsCommunicationLogAdapter().ReadAsync(
            new SourceOpenContext(path, "invalid", diagnostics.Add)))
            records.Add(record);

        Assert.Empty(records);
        var warning = Assert.Single(diagnostics);
        Assert.Equal("NDS_MALFORMED_RECORD", warning.Code);
        Assert.Contains("7-bit I2C", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_adds_readable_register_and_fw_command_metadata_without_changing_bytes()
    {
        var path = WriteCapture(
            """
            2026-07-29 14:52:41:241 Read TP 0x99060 1 0xA3
            2026-07-29 14:52:41:243 Write TP 0x99050 1 0x23
            2026-07-29 14:52:41:245 Write TP 0xFF0FE 1 0x69
            """);
        var adapter = new NdsCommunicationLogAdapter();

        var records = new List<SourceRecord>();
        await foreach (var record in adapter.ReadAsync(new SourceOpenContext(path, "registers"))) records.Add(record);

        Assert.Equal("FW State · Normal Run", records[0].SourceFields?["register_readable"]);
        Assert.Equal("51927", records[0].SourceFields?["register_profile"]);
        Assert.Equal("FW Command · Baseline Reset", records[1].SourceFields?["register_readable"]);
        Assert.Equal("0x23", records[1].SourceFields?["fw_command_code"]);
        Assert.Equal("Reset Control · Software Reset", records[2].SourceFields?["register_readable"]);
        Assert.Equal([0xA3], records[0].Data);
        Assert.Equal([0x23], records[1].Data);
        Assert.Equal([0x69], records[2].Data);
    }

    [Fact]
    public async Task Malformed_record_is_warned_dropped_and_cannot_absorb_continuation_bytes()
    {
        var path = WriteCapture(
            """
            2026-07-29 14:52:41:241 Read TP 0x99060 1 0xA3
            2026-99-29 14:52:41:243 Read TP 0x99061 1 0xEE
              0xDD
            2026-07-29 14:52:41:245 Read TP 0x99062 1 0xB4
            """);
        var diagnostics = new List<ReplayDiagnostic>();

        var records = new List<SourceRecord>();
        await foreach (var record in new NdsCommunicationLogAdapter().ReadAsync(
            new SourceOpenContext(path, "malformed", diagnostics.Add)))
        {
            records.Add(record);
        }

        Assert.Equal(2, records.Count);
        Assert.Equal([0xA3], records[0].Data);
        Assert.Equal([0xB4], records[1].Data);
        Assert.Equal("malformed:L1", records[0].StableId);
        Assert.Equal("malformed:L4", records[1].StableId);
        var warning = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("NDS_MALFORMED_RECORD", warning.Code);
        Assert.Equal(2, warning.Location.LineNumber);
    }

    [Fact]
    public async Task Reader_observes_cancellation_before_publishing_a_record()
    {
        var path = WriteCapture("2026-07-29 14:52:41:241 Read TP 0x99060 1 0xA3");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in new NdsCommunicationLogAdapter().ReadAsync(
                new SourceOpenContext(path, "cancelled"),
                cancellation.Token))
            {
            }
        });
    }

    public void Dispose()
    {
        Directory.Delete(temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteCapture(string content)
    {
        var path = Path.Combine(temporaryDirectory, "capture.txt");
        File.WriteAllText(path, content.ReplaceLineEndings(Environment.NewLine), Encoding.UTF8);
        return path;
    }
}
