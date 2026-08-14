using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Analysis;

public sealed record CommonReplayOptions(
    int CheckpointInterval,
    TimeSpan DefaultFrameInterval,
    TimeSpan IdleGapThreshold,
    TimeSpan CompressedIdleGap)
{
    public static CommonReplayOptions Default { get; } = new(
        CheckpointInterval: 256,
        DefaultFrameInterval: TimeSpan.FromMilliseconds(10),
        IdleGapThreshold: TimeSpan.FromSeconds(1),
        CompressedIdleGap: TimeSpan.FromMilliseconds(100));
}

public sealed record ReplayContact(
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    string SourceRecordId)
{
    public bool IsActive =>
        Status is TouchStatus.Enter or TouchStatus.Move ||
        Type == TouchType.Palm && Status == TouchStatus.NoFinger;
}

public sealed record ReplayTimelineEntry(
    int LogicalIndex,
    TimeSpan RecordedTime,
    TimeSpan FrameTime,
    TimeSpan DisplayTime,
    TimeSpan RecordedDelta,
    bool TimestampSynthetic,
    bool IdleGapCompressed);

public sealed record CommonReplaySnapshot(
    int LogicalIndex,
    CommonEventBufferFrame Frame,
    ReplayTimelineEntry Timeline,
    IReadOnlyList<ReplayContact> ReportedContacts,
    IReadOnlyList<ReplayContact> HostContacts,
    bool GlobalPalm,
    bool HostStateUpdated);

public sealed class CommonReplaySession
{
    private readonly IReadOnlyList<CommonEventBufferFrame> frames;
    private readonly IReadOnlyList<ReplayTimelineEntry> timeline;
    private readonly SortedDictionary<int, HostCheckpoint> checkpoints = [];
    private readonly CommonReplayOptions options;

    public CommonReplaySession(
        IReadOnlyList<CommonEventBufferFrame> frames,
        CommonReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        this.frames = frames;
        this.options = options ?? CommonReplayOptions.Default;
        ValidateOptions(this.options);
        FrameInterval = InferFrameInterval(frames, this.options.DefaultFrameInterval, this.options.IdleGapThreshold);
        timeline = BuildTimeline(frames, FrameInterval, this.options);
        Diagnostics = BuildCheckpointsAndDiagnostics();
    }

    public int Count => frames.Count;

    public TimeSpan FrameInterval { get; }

    public IReadOnlyList<ReplayTimelineEntry> Timeline => timeline;

    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }

    public CommonReplaySnapshot Seek(int logicalIndex)
    {
        if (logicalIndex < 0 || logicalIndex >= frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        }

        var checkpointPair = checkpoints.Last(pair => pair.Key <= logicalIndex);
        var state = checkpointPair.Value;
        if (checkpointPair.Key < logicalIndex)
        {
            for (var index = checkpointPair.Key + 1; index <= logicalIndex; index++)
            {
                state = Apply(state, frames[index]);
            }
        }

        var frame = frames[logicalIndex];
        return new CommonReplaySnapshot(
            logicalIndex,
            frame,
            timeline[logicalIndex],
            ReportedContacts(frame),
            state.Contacts.Values.OrderBy(contact => contact.Id).ToArray(),
            state.GlobalPalm,
            frame.HostStateEligible);
    }

    public CommonReplaySnapshot Step(int logicalIndex, int delta)
    {
        if (frames.Count == 0)
        {
            throw new InvalidOperationException("Cannot step an empty replay session.");
        }

        return Seek(Math.Clamp(logicalIndex + delta, 0, frames.Count - 1));
    }

    private IReadOnlyList<ReplayDiagnostic> BuildCheckpointsAndDiagnostics()
    {
        if (frames.Count == 0)
        {
            return [];
        }

        var state = HostCheckpoint.Empty;
        for (var index = 0; index < frames.Count; index++)
        {
            state = Apply(state, frames[index]);
            if (index % options.CheckpointInterval == 0)
            {
                checkpoints[index] = state;
            }
        }

        if (!checkpoints.ContainsKey(frames.Count - 1))
        {
            checkpoints[frames.Count - 1] = state;
        }

        var active = state.Contacts.Values.Where(contact => contact.IsActive).OrderBy(contact => contact.Id).ToArray();
        if (active.Length == 0)
        {
            return [];
        }

        var source = frames[^1].Source;
        return
        [
            new ReplayDiagnostic(
                DiagnosticSeverity.Warning,
                "CAPTURE_END_ACTIVE_CONTACTS",
                $"Capture ended with {active.Length} active contact(s); replay did not invent an All Break.",
                source.StableId,
                source.Location,
                new Dictionary<string, string>
                {
                    ["active_ids"] = string.Join(',', active.Select(contact => contact.Id)),
                })
        ];
    }

    private static HostCheckpoint Apply(HostCheckpoint prior, CommonEventBufferFrame frame)
    {
        if (!frame.HostStateEligible)
        {
            return prior;
        }

        var globalPalm = frame.DiagnosticPacket?.PalmOn == true;
        if (frame.AllBreak || globalPalm)
        {
            return new HostCheckpoint(new Dictionary<byte, ReplayContact>(), globalPalm);
        }

        var contacts = new Dictionary<byte, ReplayContact>();
        foreach (var contact in ReportedContacts(frame))
        {
            contacts[contact.Id] = contact;
        }
        return new HostCheckpoint(contacts, globalPalm);
    }

    private static ReplayContact[] ReportedContacts(CommonEventBufferFrame frame) =>
        frame.Fingers
            .Where(finger => finger.IsReported)
            .Select(finger => new ReplayContact(
                finger.Id,
                finger.Type,
                finger.Status,
                finger.X,
                finger.Y,
                frame.Source.StableId))
            .ToArray();

    private static IReadOnlyList<ReplayTimelineEntry> BuildTimeline(
        IReadOnlyList<CommonEventBufferFrame> frames,
        TimeSpan frameInterval,
        CommonReplayOptions options)
    {
        var entries = new ReplayTimelineEntry[frames.Count];
        var recorded = TimeSpan.Zero;
        var display = TimeSpan.Zero;
        DateTimeOffset? previousTimestamp = null;

        for (var index = 0; index < frames.Count; index++)
        {
            var timestamp = frames[index].Source.Timestamp;
            var synthetic = false;
            TimeSpan delta;
            if (index == 0)
            {
                delta = TimeSpan.Zero;
                synthetic = timestamp is null;
            }
            else if (timestamp is { } current && previousTimestamp is { } previous && current >= previous)
            {
                delta = current - previous;
            }
            else
            {
                delta = frameInterval;
                synthetic = true;
            }

            recorded += delta;
            var compressed = delta > options.IdleGapThreshold;
            display += compressed ? options.CompressedIdleGap : delta;
            entries[index] = new ReplayTimelineEntry(
                index,
                recorded,
                TimeSpan.FromTicks(frameInterval.Ticks * index),
                display,
                delta,
                synthetic,
                compressed);
            previousTimestamp = timestamp;
        }

        return entries;
    }

    private static TimeSpan InferFrameInterval(
        IReadOnlyList<CommonEventBufferFrame> frames,
        TimeSpan fallback,
        TimeSpan idleGapThreshold)
    {
        var deltas = new List<long>();
        DateTimeOffset? previous = null;
        foreach (var frame in frames)
        {
            if (frame.Source.Timestamp is not { } current)
            {
                continue;
            }
            if (previous is { } prior)
            {
                var delta = current - prior;
                if (delta > TimeSpan.Zero && delta <= idleGapThreshold)
                {
                    deltas.Add(delta.Ticks);
                }
            }
            previous = current;
        }

        if (deltas.Count == 0)
        {
            return fallback;
        }

        deltas.Sort();
        return TimeSpan.FromTicks(deltas[deltas.Count / 2]);
    }

    private static void ValidateOptions(CommonReplayOptions options)
    {
        if (options.CheckpointInterval <= 0 ||
            options.DefaultFrameInterval <= TimeSpan.Zero ||
            options.IdleGapThreshold <= TimeSpan.Zero ||
            options.CompressedIdleGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Replay intervals and checkpoint size must be positive.");
        }
    }

    private sealed record HostCheckpoint(
        IReadOnlyDictionary<byte, ReplayContact> Contacts,
        bool GlobalPalm)
    {
        public static HostCheckpoint Empty { get; } = new(new Dictionary<byte, ReplayContact>(), false);
    }
}
