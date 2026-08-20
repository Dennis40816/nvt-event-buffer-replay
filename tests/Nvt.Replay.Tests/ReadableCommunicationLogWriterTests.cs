using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class ReadableCommunicationLogWriterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-readable-{Guid.NewGuid():N}");

    public ReadableCommunicationLogWriterTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Writer_pairs_csv_and_jsonl_with_profile_semantics_and_original_provenance()
    {
        var record = NvtRegisterCatalog.Reannotate(new SourceRecord(
            12,
            "capture:L42",
            DateTimeOffset.Parse("2026-08-14T10:00:00+08:00"),
            BusOperation.Write,
            "TP",
            0x80850,
            1,
            [0x23],
            "original remains outside derived export",
            new SourceLocation(1234, 42)), "51929/51932");

        var result = await new ReadableCommunicationLogWriter().WriteAsync(directory, [record]);
        var csv = await File.ReadAllTextAsync(result.CsvPath);
        var jsonl = await File.ReadAllTextAsync(result.JsonlPath);

        Assert.Equal(1, result.RecordCount);
        Assert.Equal(1, result.AnnotatedRegisterCount);
        Assert.Equal(0, result.AmbiguousRegisterCount);
        Assert.Contains("51929/51932", csv);
        Assert.Contains("FW Command", csv);
        Assert.Contains("Baseline Reset", csv);
        Assert.Contains("\"lineNumber\":42", jsonl);
        Assert.Contains("\"rawHex\":\"23\"", jsonl);
        Assert.DoesNotContain("original remains", csv);
    }

    [Fact]
    public async Task Writer_overlays_selected_profile_without_modifying_the_source_record()
    {
        var record = new SourceRecord(
            13,
            "capture:L43",
            null,
            BusOperation.Read,
            "TP",
            0x99000,
            1,
            [0x11],
            "raw source remains unchanged",
            new SourceLocation(1300, 43));
        var records = new[] { record };
        var projection = new RegisterAnnotationIndex(records).Project(NvtRegisterCatalog.FindProfile("51927"));

        var result = await new ReadableCommunicationLogWriter().WriteAsync(directory, records, projection);
        var csv = await File.ReadAllTextAsync(result.CsvPath);

        Assert.Contains("51927", csv);
        Assert.Contains("Event Buffer", csv);
        Assert.Same(record, records[0]);
        Assert.Null(record.SourceFields);
        Assert.Equal("raw source remains unchanged", record.RawText);
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
