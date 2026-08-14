using System.Text;
using Nvt.Replay.Analysis;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Tests;

public sealed class Desay97NdsInspectorTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), $"nvt-desay-{Guid.NewGuid():N}");

    public Desay97NdsInspectorTests() => Directory.CreateDirectory(temporaryDirectory);

    [Fact]
    public async Task Nds_inspection_assembles_two_reads_into_one_traceable_frame()
    {
        var payload = new byte[] { 0x01, 0x61, 0x03, 0x14, 0x07, 0x19 };
        var full = payload.Append(Crc8Poly1D.Compute(payload)).ToArray();
        var path = WriteLog(Record([0x01], 0) + Environment.NewLine + Record(full, 10));

        var report = await new Desay97NdsInspector().InspectAsync(path, Desay97Profile.Standard);

        var frame = Assert.Single(report.Frames);
        Assert.Equal(2, frame.Packet.PhysicalRecords.Count);
        Assert.Equal(1, frame.Packet.Probe.Location.LineNumber);
        Assert.Equal(2, frame.Packet.PayloadRead.Location.LineNumber);
        Assert.Empty(report.Diagnostics);
    }

    public void Dispose()
    {
        Directory.Delete(temporaryDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string WriteLog(string content)
    {
        var path = Path.Combine(temporaryDirectory, "desay.txt");
        File.WriteAllText(path, content, Encoding.UTF8);
        return path;
    }

    private static string Record(IReadOnlyList<byte> data, int millisecond)
    {
        var bytes = string.Join(' ', data.Select(value => $"0x{value:X2}"));
        return $"2026-08-14 10:00:00:{millisecond:000} Read TP 0x99000 {data.Count} {bytes}";
    }
}
