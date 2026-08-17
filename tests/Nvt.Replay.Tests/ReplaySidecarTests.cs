using System.Security.Cryptography;
using System.Text;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class ReplaySidecarTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-sidecar-{Guid.NewGuid():N}");

    public ReplaySidecarTests() => Directory.CreateDirectory(directory);

    [Fact]
    public async Task Marker_review_QA_and_evidence_round_trip_with_portable_path()
    {
        var evidencePath = Path.Combine(directory, "kernel.log");
        await File.WriteAllTextAsync(evidencePath, "PID1615 synthetic kernel evidence");
        var evidenceHash = Hash(await File.ReadAllBytesAsync(evidencePath));
        var sidecarPath = Path.Combine(directory, "capture.nvtreplay.json");
        var configuration = Configuration() with { RegisterProfile = "51927" };
        var document = Document(configuration,
        [
            new ReplayMarker(
                "marker-1", "ASIL range", 10, 12, DateTimeOffset.Parse("2026-08-14T10:00:00+08:00"),
                "QA evidence", ["QA-1615-007"],
                [new ReplayEvidenceReference("evidence-1", ReplayEvidenceKind.KernelLog, evidencePath, evidenceHash, "Kernel log")]),
        ],
        [new ReviewStateSnapshot("COMMON_ASIL", ReviewWorkflowState.Resolved, ReviewDisposition.Expected)]);
        var store = new ReplaySidecarStore();

        await store.SaveAsync(sidecarPath, document);
        var json = await File.ReadAllTextAsync(sidecarPath);
        var loaded = await store.LoadAsync(sidecarPath, document.SourceSha256, configuration);

        Assert.DoesNotContain(evidencePath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"isRange\"", json, StringComparison.OrdinalIgnoreCase);
        var marker = Assert.Single(loaded.Document.Markers);
        Assert.True(marker.IsRange);
        Assert.Equal(["QA-1615-007"], marker.QaCaseIds);
        var evidence = Assert.Single(loaded.Evidence);
        Assert.True(evidence.Exists);
        Assert.True(evidence.HashMatches);
        Assert.Equal(Path.GetFullPath(evidencePath), evidence.ResolvedPath);
        Assert.False(loaded.RequiresExplicitConfirmation);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(ReviewDisposition.Expected, Assert.Single(loaded.Document.ReviewStates).Disposition);
        Assert.Equal("51927", loaded.Document.DecodeConfiguration.RegisterProfile);
    }

    [Fact]
    public async Task Modified_capture_configuration_and_missing_evidence_are_visible_mismatches()
    {
        var sidecarPath = Path.Combine(directory, "capture.nvtreplay.json");
        var configuration = Configuration();
        var document = Document(configuration,
        [
            new ReplayMarker("marker-1", "missing", 0, 0, DateTimeOffset.UnixEpoch, Evidence:
                [new ReplayEvidenceReference("missing-1", ReplayEvidenceKind.FirmwareLog, "missing.log")]),
        ]);
        var store = new ReplaySidecarStore();
        await store.SaveAsync(sidecarPath, document);

        var loaded = await store.LoadAsync(
            sidecarPath,
            new string('f', 64),
            configuration with { EventBufferVersion = "0x84" });

        Assert.True(loaded.RequiresExplicitConfirmation);
        Assert.False(loaded.SourceHashMatches);
        Assert.False(loaded.DecodeConfigurationMatches);
        Assert.False(Assert.Single(loaded.Evidence).Exists);
        Assert.Equal(3, loaded.Warnings.Count);
    }

    [Fact]
    public async Task Unsupported_future_schema_fails_safely()
    {
        var path = Path.Combine(directory, "future.nvtreplay.json");
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":99}");

        var error = await Assert.ThrowsAsync<UnsupportedReplaySidecarVersionException>(() =>
            new ReplaySidecarStore().LoadAsync(path, new string('0', 64), Configuration()));

        Assert.Equal(99, error.Version);
        Assert.Contains("newer", error.Message);
    }

    [Fact]
    public async Task Interrupted_atomic_write_preserves_prior_output_and_removes_temporary_file()
    {
        var path = Path.Combine(directory, "stable.json");
        await File.WriteAllTextAsync(path, "original");
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicOutput.WriteAsync(
            path,
            async (stream, token) =>
            {
                await stream.WriteAsync("replacement"u8.ToArray(), token);
                cancellation.Cancel();
            },
            cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory, ".stable.json.*.tmp"));
    }

    [Fact]
    public void Review_state_import_does_not_rewrite_diagnostics_and_reports_stale_groups()
    {
        var diagnostic = new ReplayDiagnostic(DiagnosticSeverity.Warning, "TEST", "evidence", "source", new SourceLocation(1, 1));
        var session = new ReviewSession([diagnostic]);
        var groupId = Assert.Single(session.Groups).Id;

        var unresolved = session.ImportState(
        [
            new ReviewStateSnapshot(groupId, ReviewWorkflowState.Acknowledged, ReviewDisposition.FalsePositive),
            new ReviewStateSnapshot("missing-group", ReviewWorkflowState.Resolved, ReviewDisposition.Expected),
        ]);

        Assert.Equal(["missing-group"], unresolved);
        Assert.Equal(ReviewDisposition.FalsePositive, session.Find(groupId).Disposition);
        Assert.Same(diagnostic, Assert.Single(session.Diagnostics));
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static ReplayDecodeConfiguration Configuration() =>
        new("0x83", null, "nds-communication-log");

    private static ReplaySidecarDocument Document(
        ReplayDecodeConfiguration configuration,
        IReadOnlyList<ReplayMarker>? markers = null,
        IReadOnlyList<ReviewStateSnapshot>? review = null) =>
        new(
            ReplaySidecarDocument.CurrentSchemaVersion,
            new string('a', 64),
            "capture.txt",
            configuration,
            markers ?? [],
            review ?? [],
            new Dictionary<string, bool> { ["pauseOnAlarm"] = true },
            DateTimeOffset.Parse("2026-08-14T10:00:00+08:00"));

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));
}
