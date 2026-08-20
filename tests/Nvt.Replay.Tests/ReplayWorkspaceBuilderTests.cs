using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class ReplayWorkspaceBuilderTests
{
    [Fact]
    public void Hundred_thousand_frames_are_materialized_once_in_linear_order()
    {
        const int frameCount = 100_000;
        var replay = new CountingReplay(frameCount);

        var result = new ReplayWorkspaceBuilder().Build(
            new ReplayWorkspaceBuildRequest(replay, ProgressInterval: 4096));

        Assert.Equal(frameCount, result.MaterializedFrames);
        Assert.Equal(frameCount, result.Workspace.Frames.Count);
        Assert.Equal(frameCount, result.Workspace.FrameStates.Count);
        Assert.Equal(frameCount, replay.EnumerationCount);
        Assert.Equal(0, replay.SeekCount);
        Assert.Equal(0, result.Workspace.Frames[0].LogicalIndex);
        Assert.Equal(frameCount - 1, result.Workspace.Frames[frameCount - 1].LogicalIndex);
        Assert.Equal(2816, result.Workspace.Extent.MaximumX);
        Assert.Equal(1792, result.Workspace.Extent.MaximumY);
        Assert.Equal(1u << 1, result.Workspace.FrameStates[0].ActiveContactMask);
        Assert.True(result.Workspace.FrameStates[frameCount / 2].AllBreak);
        Assert.True(result.Workspace.FrameStates[^1].HasReportedBreak);
        Assert.Equal(
            frameCount / 2,
            result.Workspace.AutoPauseIndex.FirstAfter(0, frameCount - 1, pauseOnBreak: false, pauseOnAllBreak: true)?.LogicalIndex);
        Assert.Equal(
            frameCount - 1,
            result.Workspace.AutoPauseIndex.FirstAfter(0, frameCount - 1, pauseOnBreak: true, pauseOnAllBreak: false)?.LogicalIndex);
    }

    [Fact]
    public void Cancelled_rebuild_does_not_mutate_the_previous_workspace()
    {
        var builder = new ReplayWorkspaceBuilder();
        var previous = builder.Build(new ReplayWorkspaceBuildRequest(new CountingReplay(3))).Workspace;
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<ReplayWorkspaceBuildProgress>(item =>
        {
            if (item.CompletedFrames >= 256) cancellation.Cancel();
        });

        Assert.ThrowsAny<OperationCanceledException>(() => builder.Build(
            new ReplayWorkspaceBuildRequest(new CountingReplay(10_000), ProgressInterval: 128),
            progress,
            cancellation.Token));

        Assert.Equal(3, previous.Frames.Count);
        Assert.Equal("source-0", previous.Frames[0].PrimarySource.StableId);
        Assert.Equal("source-2", previous.Frames[2].PrimarySource.StableId);
    }

    [Fact]
    public void Non_linear_snapshot_sequence_is_rejected_without_a_partial_result()
    {
        var replay = new CountingReplay(3, logicalIndexOffset: 1);

        var error = Assert.Throws<InvalidDataException>(() =>
            new ReplayWorkspaceBuilder().Build(new ReplayWorkspaceBuildRequest(replay)));

        Assert.Contains("expected logical index 0", error.Message);
    }

    private sealed class CountingReplay(int count, int logicalIndexOffset = 0) : ITouchReplaySession
    {
        public int Count { get; } = count;
        public int EnumerationCount { get; private set; }
        public int SeekCount { get; private set; }
        public TimeSpan FrameInterval => TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 120);
        public IReadOnlyList<ReplayTimelineEntry> Timeline { get; } = Enumerable.Range(0, count)
            .Select(index => TimelineEntry(index))
            .ToArray();
        public IReadOnlyList<ReplayDiagnostic> Diagnostics => [];
        public IReadOnlyList<ReplayContact> AllReportedContacts => [];

        public ITouchReplaySnapshot Seek(int logicalIndex)
        {
            SeekCount++;
            return Snapshot(logicalIndex + logicalIndexOffset, logicalIndex);
        }

        public IEnumerable<ITouchReplaySnapshot> EnumerateSnapshots(CancellationToken cancellationToken = default)
        {
            for (var index = 0; index < Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnumerationCount++;
                yield return Snapshot(index + logicalIndexOffset, index);
            }
        }

        private FakeSnapshot Snapshot(int logicalIndex, int sourceIndex)
        {
            IReadOnlyList<ReplayContact> reported = sourceIndex switch
            {
                0 => [new ReplayContact(1, TouchType.Finger, TouchStatus.Enter, 100, 200, $"source-{sourceIndex}")],
                var index when index == Count - 1 =>
                    [new ReplayContact(2, TouchType.Finger, TouchStatus.Break, 2500, 1500, $"source-{sourceIndex}")],
                _ => [],
            };
            var host = sourceIndex == 0 ? reported : [];
            var source = new SourceRecord(
                sourceIndex,
                $"source-{sourceIndex}",
                DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(FrameInterval.Ticks * sourceIndex),
                BusOperation.Paint,
                "TP",
                1,
                80,
                [],
                string.Empty,
                new SourceLocation(sourceIndex, sourceIndex + 1));
            return new FakeSnapshot(
                logicalIndex,
                source,
                Timeline[sourceIndex],
                reported,
                host,
                sourceIndex == Count / 2);
        }

        private static ReplayTimelineEntry TimelineEntry(int index)
        {
            var time = TimeSpan.FromTicks((TimeSpan.TicksPerSecond / 120) * index);
            return new ReplayTimelineEntry(
                index,
                time,
                time,
                time,
                index == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 120),
                false,
                false);
        }
    }

    private sealed record FakeSnapshot(
        int LogicalIndex,
        SourceRecord PrimarySource,
        ReplayTimelineEntry Timeline,
        IReadOnlyList<ReplayContact> ReportedContacts,
        IReadOnlyList<ReplayContact> HostContacts,
        bool AllBreak) : ITouchReplaySnapshot
    {
        public object DecodedFrame => this;
        public IReadOnlyList<SourceRecord> PhysicalRecords => [PrimarySource];
        public bool GlobalPalm => false;
        public bool HostStateUpdated => true;
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
