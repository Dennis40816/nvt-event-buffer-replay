using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class SidecarEvidenceLimitTests
{
    [Fact]
    public async Task Evidence_above_the_size_limit_is_reported_as_not_verified_and_the_review_still_opens()
    {
        var token = Guid.NewGuid().ToString("N");
        var evidencePath = Path.Combine(AppContext.BaseDirectory, $"evidence-limit-{token}.log");
        var sidecarPath = Path.Combine(AppContext.BaseDirectory, $"evidence-limit-{token}.nvtreplay.json");
        try
        {
            await using (var large = new FileStream(evidencePath, FileMode.Create, FileAccess.Write))
                large.SetLength(FileHasher.MaximumFileBytes + 1);
            var configuration = new ReplayDecodeConfiguration("0x83", null, "nds-communication-log");
            var marker = new ReplayMarker("marker-1", "large", 0, 0, DateTimeOffset.UnixEpoch, Evidence:
                [new ReplayEvidenceReference("large-1", ReplayEvidenceKind.KernelLog, evidencePath, new string('a', 64))]);
            var document = new ReplaySidecarDocument(
                ReplaySidecarDocument.CurrentSchemaVersion,
                new string('b', 64),
                "capture.txt",
                configuration,
                [marker],
                [],
                new Dictionary<string, bool>(),
                DateTimeOffset.UnixEpoch);
            var store = new ReplaySidecarStore();
            await store.SaveAsync(sidecarPath, document);

            var loaded = await store.LoadAsync(sidecarPath, document.SourceSha256, configuration);

            var evidence = Assert.Single(loaded.Evidence);
            Assert.True(evidence.Exists);
            Assert.False(evidence.HashMatches);
            Assert.NotNull(evidence.NotVerifiedReason);
            var warning = Assert.Single(loaded.Warnings);
            Assert.StartsWith("Evidence not verified: ", warning, StringComparison.Ordinal);
            Assert.False(loaded.RequiresExplicitConfirmation);
        }
        finally
        {
            File.Delete(evidencePath);
            File.Delete(sidecarPath);
        }
    }
}
