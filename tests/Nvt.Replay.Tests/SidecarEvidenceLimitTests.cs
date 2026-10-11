using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class SidecarEvidenceLimitTests
{
    private const long SmallLimit = 16;

    [Fact]
    public async Task Evidence_above_the_size_limit_is_reported_as_not_verified_and_the_review_still_opens()
    {
        var store = new ReplaySidecarStore((path, token) => FileHasher.ComputeSha256Async(path, token, SmallLimit));

        var loaded = await LoadWithEvidenceAsync(store, new byte[SmallLimit + 1]);

        var evidence = Assert.Single(loaded.Evidence);
        Assert.True(evidence.Exists);
        Assert.False(evidence.HashMatches);
        Assert.Equal("The file is 17 bytes. The maximum is 16 bytes.", evidence.NotVerifiedReason);
        Assert.False(loaded.RequiresExplicitConfirmation);
    }

    [Fact]
    public async Task Evidence_within_the_size_limit_is_hashed_normally()
    {
        var store = new ReplaySidecarStore((path, token) => FileHasher.ComputeSha256Async(path, token, SmallLimit));

        var loaded = await LoadWithEvidenceAsync(store, new byte[SmallLimit]);

        var evidence = Assert.Single(loaded.Evidence);
        Assert.Null(evidence.NotVerifiedReason);
        Assert.False(evidence.HashMatches);
        Assert.Equal("Evidence SHA-256 mismatch: evidence.log", Assert.Single(loaded.Warnings));
    }

    [Fact]
    public async Task Evidence_that_changes_while_it_is_read_is_reported_as_not_verified_and_the_review_still_opens()
    {
        var store = new ReplaySidecarStore((_, _) => throw new FileNotHashableException(
            "The file changed while it was read.",
            "evidence.log: The file changed while it was read. Copy the file and load the copy."));

        var loaded = await LoadWithEvidenceAsync(store, [1, 2, 3]);

        var evidence = Assert.Single(loaded.Evidence);
        Assert.Equal("The file changed while it was read.", evidence.NotVerifiedReason);
        Assert.False(loaded.RequiresExplicitConfirmation);
    }

    [Fact]
    public async Task The_not_verified_warning_names_the_file_once()
    {
        var store = new ReplaySidecarStore((path, token) => FileHasher.ComputeSha256Async(path, token, SmallLimit));

        var loaded = await LoadWithEvidenceAsync(store, new byte[SmallLimit + 1]);

        var warning = Assert.Single(loaded.Warnings);
        Assert.Equal("Evidence not verified: evidence.log (The file is 17 bytes. The maximum is 16 bytes.)", warning);
    }

    private static async Task<ReplaySidecarOpenResult> LoadWithEvidenceAsync(ReplaySidecarStore store, byte[] evidenceBytes)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, $"evidence-limit-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(folder, "evidence.log");
        var sidecarPath = Path.Combine(folder, "capture.nvtreplay.json");
        try
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllBytesAsync(evidencePath, evidenceBytes);
            var configuration = new ReplayDecodeConfiguration("0x83", null, "nds-communication-log");
            var marker = new ReplayMarker("marker-1", "evidence", 0, 0, DateTimeOffset.UnixEpoch, Evidence:
                [new ReplayEvidenceReference("evidence-1", ReplayEvidenceKind.KernelLog, "evidence.log", new string('a', 64))]);
            var document = new ReplaySidecarDocument(
                ReplaySidecarDocument.CurrentSchemaVersion,
                new string('b', 64),
                "capture.txt",
                configuration,
                [marker],
                [],
                new Dictionary<string, bool>(),
                DateTimeOffset.UnixEpoch);
            await store.SaveAsync(sidecarPath, document);
            return await store.LoadAsync(sidecarPath, document.SourceSha256, configuration);
        }
        finally
        {
            File.Delete(evidencePath);
            File.Delete(sidecarPath);
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    }
}
