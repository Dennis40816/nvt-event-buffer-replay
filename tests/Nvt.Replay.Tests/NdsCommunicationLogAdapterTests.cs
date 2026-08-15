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
