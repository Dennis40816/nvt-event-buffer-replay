using System.Text;
using Nvt.Replay.Analysis;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Tests;

public sealed class CommonNdsInspectorTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"nvt-inspect-{Guid.NewGuid():N}");

    public CommonNdsInspectorTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
    }

    [Fact]
    public async Task Inspection_ignores_direct_memory_and_decodes_Paint_event()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        var path = WriteLog(
            Record("Read", "0x99070", [0x01, 0x00]) + Environment.NewLine +
            Record("Paint", "0x01", packet));

        var report = await new CommonNdsInspector().InspectAsync(path, CommonEventBufferVersion.V83);

        var frame = Assert.Single(report.Frames);
        Assert.True(frame.AllBreak);
        Assert.True(frame.CrcValid);
        Assert.Empty(report.Diagnostics);
        Assert.Matches("^[0-9a-f]{64}$", report.SourceSha256);
    }

    [Fact]
    public async Task Inspection_discards_wrong_byte_count_with_diagnostic()
    {
        var path = WriteLog(Record("Paint", "0x01", [0x00]));

        var report = await new CommonNdsInspector().InspectAsync(path, CommonEventBufferVersion.V83);

        Assert.Empty(report.Frames);
        Assert.Contains(report.Diagnostics, item => item.Code == "NDS_BYTE_COUNT_MISMATCH");
        Assert.Contains(report.Diagnostics, item => item.Code == "NO_EVENT_BUFFER_RECORDS");
    }

    [Fact]
    public async Task Inspection_does_not_feed_crc_failed_frame_into_stream_monitor()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        packet[73] = 0x1A;
        packet[79] ^= 0xFF;
        var path = WriteLog(Record("Paint", "0x01", packet));

        var report = await new CommonNdsInspector().InspectAsync(path, CommonEventBufferVersion.V83);

        Assert.Contains(report.Diagnostics, item => item.Code == "COMMON_CRC_MISMATCH");
        Assert.DoesNotContain(report.Diagnostics, item => item.Code == "COMMON_ASIL_ALARM");
        Assert.False(Assert.Single(report.Frames).HostStateEligible);
    }

    public void Dispose()
    {
        Directory.Delete(temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteLog(string content)
    {
        var path = Path.Combine(temporaryDirectory, "capture.txt");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string Record(string operation, string address, IReadOnlyList<byte> data)
    {
        var bytes = string.Join(' ', data.Select(value => $"0x{value:X2}"));
        return $"2026-08-14 10:00:00:000 {operation} TP {address} {data.Count} {bytes}";
    }
}
