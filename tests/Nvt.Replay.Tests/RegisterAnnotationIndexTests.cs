using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class RegisterAnnotationIndexTests
{
    [Fact]
    public void Same_profile_name_with_different_content_hashes_cannot_share_annotations()
    {
        var records = new[]
        {
            Record(0, 0x1000),
            Record(1, 0x2000),
        };
        var index = new RegisterAnnotationIndex(records);
        var firstProfile = new NvtRegisterProfile("custom", 0x1000, 0x3000, 0x4000);
        var secondProfile = new NvtRegisterProfile("custom", 0x2000, 0x5000, 0x6000);

        var first = index.Project(firstProfile);
        var second = index.Project(secondProfile);

        Assert.Equal(first.ProfileIdentity.Name, second.ProfileIdentity.Name);
        Assert.NotEqual(first.ProfileIdentity.ContentHash, second.ProfileIdentity.ContentHash);
        Assert.True(first.TryGet(records[0], out _));
        Assert.False(first.TryGet(records[1], out _));
        Assert.False(second.TryGet(records[0], out _));
        Assert.True(second.TryGet(records[1], out _));
    }

    [Fact]
    public void Cancellation_does_not_publish_a_partial_projection()
    {
        using var cancellation = new CancellationTokenSource();
        var records = new CancellingReadOnlyList(
            Enumerable.Range(0, 4).Select(index => Record(index, (uint)(0x1000 + index))).ToArray(),
            cancellation,
            cancelAtIndex: 1);
        var index = new RegisterAnnotationIndex(records);
        var profile = new NvtRegisterProfile("custom", 0x1000, 0x3000, 0x4000);

        Assert.ThrowsAny<OperationCanceledException>(() => index.Project(profile, cancellation.Token));

        var completed = index.Project(profile);
        Assert.Equal(4, completed.Count);
    }

    private static SourceRecord Record(int index, uint address) => new(
        index,
        $"source-{index}",
        null,
        BusOperation.Read,
        "TP",
        address,
        1,
        [0x00],
        $"raw-{index}",
        new SourceLocation(index, index + 1));

    private sealed class CancellingReadOnlyList(
        IReadOnlyList<SourceRecord> records,
        CancellationTokenSource cancellation,
        int cancelAtIndex) : IReadOnlyList<SourceRecord>
    {
        public int Count => records.Count;

        public SourceRecord this[int index]
        {
            get
            {
                if (index == cancelAtIndex) cancellation.Cancel();
                return records[index];
            }
        }

        public IEnumerator<SourceRecord> GetEnumerator() => records.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
