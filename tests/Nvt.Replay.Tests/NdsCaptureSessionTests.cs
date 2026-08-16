using System.Text;
using Nvt.Replay.Analysis;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Tests;

public sealed class NdsCaptureSessionTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"nvt-session-{Guid.NewGuid():N}");

    public NdsCaptureSessionTests()
    {
        Directory.CreateDirectory(temporaryDirectory);
    }

    [Fact]
    public async Task DecodeCommon_reuses_indexed_records_after_source_is_removed()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        var path = WriteLog(Record("Paint", "0x01", packet));

        var session = await CaptureSession.LoadAsync(path);
        File.Delete(path);

        var report = session.DecodeCommon(CommonEventBufferVersion.V83);

        Assert.Single(session.Records);
        Assert.Single(report.Frames);
        Assert.True(report.Frames[0].CrcValid);
    }

    [Fact]
    public async Task LoadAsync_rejects_source_without_NDS_records()
    {
        var path = WriteLog("this is not an NDS communication log");

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => CaptureSession.LoadAsync(path));

        Assert.Contains("supported capture-source schema", error.Message);
    }

    [Fact]
    public async Task LoadAsync_reports_probe_index_and_ready_phases()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        var path = WriteLog(Record("Paint", "0x01", packet));
        var updates = new List<CaptureLoadProgress>();
        var progress = new InlineProgress<CaptureLoadProgress>(updates.Add);

        await CaptureSession.LoadAsync(path, progress);

        Assert.Equal(
            ["Probing source", "Hashing source", "Indexing records", "Ready for configuration"],
            updates.Select(update => update.Phase));
        Assert.Equal(1, updates[^1].RecordsRead);
    }

    [Fact]
    public async Task LoadAsync_observes_cancellation_without_publishing_a_partial_session()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        var path = WriteLog(string.Join(Environment.NewLine, Enumerable.Repeat(Record("Paint", "0x01", packet), 1000)));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CaptureSession.LoadAsync(path, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Colliding_event_buffer_address_decodes_only_after_operator_selects_register_profile()
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        var path = WriteLog(Record("Read", "0x80800", packet));

        var unresolved = await CaptureSession.LoadAsync(path);
        var resolved = unresolved.WithRegisterProfile("51929/51932");

        Assert.Empty(unresolved.DecodeCommon(CommonEventBufferVersion.V83).Frames);
        Assert.Single(resolved.DecodeCommon(CommonEventBufferVersion.V83).Frames);
        Assert.Equal("51929/51932", resolved.RegisterProfile);
        Assert.Equal("Event Buffer", resolved.Records[0].SourceFields?["register_region"]);
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
