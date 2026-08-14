using System.Security.Cryptography;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class CaptureAnalysisTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-analysis-{Guid.NewGuid():N}");

    public CaptureAnalysisTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Analysis_preserves_event_links_clock_domains_ASIL_and_valid_reported_hotspots()
    {
        var replay = Replay();
        var diagnostics = Diagnostics(replay);
        var review = new ReviewSession(diagnostics, new Dictionary<string, int> { ["source-0"] = 0, ["source-1"] = 1 });
        review.Acknowledge(Assert.Single(review.Groups).Id);

        var report = new CaptureAnalyzer().Analyze(
            "capture.txt",
            new string('a', 64),
            new ReplayDecodeConfiguration("0x83", null, "synthetic"),
            EvidenceStatus.Verified,
            replay,
            diagnostics,
            review,
            heatmapColumns: 4,
            heatmapRows: 2,
            heatmapPixelWidth: 40,
            heatmapPixelHeight: 20);

        Assert.Equal("captured", report.Manifest.Clock.Domain);
        Assert.Equal(TimeSpan.FromMilliseconds(10), report.Manifest.Clock.CapturedDuration);
        Assert.Equal(2, report.Events.Count);
        Assert.All(report.Diagnostics, item => Assert.Single(item.EventIds));
        Assert.Equal(1, report.Asil.Assertions);
        Assert.Equal(1, report.Asil.Clears);
        Assert.Equal(TimeSpan.FromMilliseconds(10), Assert.Single(report.Asil.Periods).CapturedDuration);
        Assert.Equal(1, report.Asil.AcknowledgedGroups);
        Assert.Equal(2, report.Hotspot.SampleCount);
        Assert.Equal(2, report.Hotspot.Counts.Sum());
        Assert.Equal("linear-origin-top-left", report.Manifest.HeatmapTransform.Kind);
    }

    [Fact]
    public void Selected_range_excludes_diagnostics_linked_only_to_outside_frames()
    {
        var replay = Replay();
        var report = new CaptureAnalyzer().Analyze(
            "capture.txt", new string('a', 64), new ReplayDecodeConfiguration("0x83", null, "synthetic"),
            EvidenceStatus.Verified, replay, Diagnostics(replay), range: new AnalysisRange(1, 1));

        Assert.Equal(["COMMON_ASIL_CLEARED"], report.Diagnostics.Select(item => item.Code));
        Assert.Equal(1, Assert.Single(report.Events).LogicalIndex);
    }

    [Fact]
    public async Task Analysis_outputs_are_complete_and_heatmap_is_deterministic()
    {
        var replay = Replay();
        var diagnostics = Diagnostics(replay);
        var report = new CaptureAnalyzer().Analyze(
            "capture.txt",
            new string('a', 64),
            new ReplayDecodeConfiguration("0x83", null, "synthetic"),
            EvidenceStatus.Verified,
            replay,
            diagnostics,
            heatmapColumns: 4,
            heatmapRows: 2,
            heatmapPixelWidth: 40,
            heatmapPixelHeight: 20);
        var writer = new AnalysisOutputWriter();

        var first = await writer.WriteAsync(Path.Combine(directory, "first"), report);
        var second = await writer.WriteAsync(Path.Combine(directory, "second"), report);

        foreach (var path in new[] { first.ReportJson, first.EventsJson, first.EventsCsv, first.DiagnosticsJson, first.DiagnosticsCsv, first.ManifestJson, first.HeatmapPng })
            Assert.True(File.Exists(path), path);
        Assert.Equal(Hash(first.HeatmapPng), Hash(second.HeatmapPng));
        var goldenHash = Hash(first.HeatmapPng);
        Assert.Equal("d641a1948d29207a90e832a42066bd13c2b4139600cd5b80ecb333e6295c27e6", goldenHash);
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], (await File.ReadAllBytesAsync(first.HeatmapPng))[..8]);
        Assert.Contains("event-00000000-source-0", await File.ReadAllTextAsync(first.EventsCsv));
        Assert.Contains("heatmapTransform", await File.ReadAllTextAsync(first.ManifestJson));
    }

    [Fact]
    public async Task Cancelled_output_preserves_prior_file_and_removes_temporary_file()
    {
        var output = Path.Combine(directory, "cancelled");
        Directory.CreateDirectory(output);
        var reportPath = Path.Combine(output, "analysis-report.json");
        await File.WriteAllTextAsync(reportPath, "prior-valid-report");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AnalysisOutputWriter().WriteAsync(
            output,
            new CaptureAnalyzer().Analyze(
                "capture.txt", new string('a', 64), new ReplayDecodeConfiguration("0x83", null, "synthetic"),
                EvidenceStatus.Verified, Replay(), Diagnostics(Replay())),
            cancellation.Token));

        Assert.Equal("prior-valid-report", await File.ReadAllTextAsync(reportPath));
        Assert.Empty(Directory.GetFiles(output, ".*.tmp"));
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<ReplayDiagnostic> Diagnostics(ITouchReplaySession replay) =>
    [
        new(DiagnosticSeverity.Alarm, "COMMON_ASIL_ALARM", "asserted", "source-0", new SourceLocation(10, 1)),
        new(DiagnosticSeverity.Info, "COMMON_ASIL_CLEARED", "cleared", "source-1", new SourceLocation(20, 2)),
    ];

    private static FakeReplay Replay()
    {
        var source0 = new SourceRecord(0, "source-0", DateTimeOffset.Parse("2026-08-14T10:00:00Z"), BusOperation.Paint, "TP", 1, 80, [], "", new SourceLocation(10, 1));
        var source1 = new SourceRecord(1, "source-1", DateTimeOffset.Parse("2026-08-14T10:00:00.010Z"), BusOperation.Paint, "TP", 1, 80, [], "", new SourceLocation(20, 2));
        var contact0 = new ReplayContact(1, TouchType.Finger, TouchStatus.Enter, 100, 200, source0.StableId);
        var contact1 = new ReplayContact(1, TouchType.Finger, TouchStatus.Move, 200, 250, source1.StableId);
        return new FakeReplay(
        [
            Snapshot(0, source0, TimeSpan.Zero, contact0),
            Snapshot(1, source1, TimeSpan.FromMilliseconds(10), contact1),
        ]);
    }

    private static ITouchReplaySnapshot Snapshot(int index, SourceRecord source, TimeSpan time, ReplayContact contact) =>
        new FakeSnapshot(index, source, new ReplayTimelineEntry(index, time, time, time, index == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(10), false, false), [contact]);

    private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record FakeSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        ReplayTimelineEntry Timeline,
        IReadOnlyList<ReplayContact> ReportedContacts) : ITouchReplaySnapshot
    {
        public object DecodedFrame => this;
        public IReadOnlyList<SourceRecord> PhysicalRecords => [PrimarySource];
        public IReadOnlyList<ReplayContact> HostContacts => ReportedContacts;
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }

    private sealed class FakeReplay(IReadOnlyList<ITouchReplaySnapshot> snapshots) : ITouchReplaySession
    {
        public int Count => snapshots.Count;
        public TimeSpan FrameInterval => TimeSpan.FromMilliseconds(10);
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; } = snapshots.Select(item => item.Timeline).ToArray();
        public IReadOnlyList<ReplayDiagnostic> Diagnostics => [];
        public IReadOnlyList<ReplayContact> AllReportedContacts { get; } = snapshots.SelectMany(item => item.ReportedContacts).ToArray();
        public ITouchReplaySnapshot Seek(int logicalIndex) => snapshots[logicalIndex];
    }
}
