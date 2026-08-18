using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

public sealed record TouchReplayOptions(
    int CheckpointInterval,
    TimeSpan DefaultFrameInterval,
    TimeSpan IdleGapThreshold,
    TimeSpan CompressedIdleGap)
{
    public static TimeSpan NominalTpFrameInterval { get; } =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 120);

    public static TouchReplayOptions Default { get; } = new(
        256,
        NominalTpFrameInterval,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(100));
}

public sealed record ReplayContact(
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    string SourceRecordId,
    bool Invalid = false)
{
    public bool IsActive =>
        !Invalid &&
        (Status is TouchStatus.Enter or TouchStatus.Move ||
         Type == TouchType.Palm && Status == TouchStatus.NoFinger);
}

public sealed record ReplayTimelineEntry(
    int LogicalIndex,
    TimeSpan RecordedTime,
    TimeSpan FrameTime,
    TimeSpan DisplayTime,
    TimeSpan RecordedDelta,
    bool TimestampSynthetic,
    bool IdleGapCompressed);

public sealed record TouchReplayFrameProjection(
    SourceRecord PrimarySource,
    IReadOnlyList<SourceRecord> PhysicalRecords,
    IReadOnlyList<ReplayContact> ReportedContacts,
    IReadOnlyList<ReplayContact> HostReportedContacts,
    bool AllBreak,
    bool GlobalPalm,
    bool HostStateEligible);

public interface ITouchReplaySnapshot
{
    int LogicalIndex { get; }
    object DecodedFrame { get; }
    SourceRecord PrimarySource { get; }
    IReadOnlyList<SourceRecord> PhysicalRecords { get; }
    ReplayTimelineEntry Timeline { get; }
    IReadOnlyList<ReplayContact> ReportedContacts { get; }
    IReadOnlyList<ReplayContact> HostContacts { get; }
    bool AllBreak => false;
    bool GlobalPalm { get; }
    bool HostStateUpdated { get; }
}

public sealed record TouchReplaySnapshot<TFrame>(
    int LogicalIndex,
    TFrame Frame,
    TouchReplayFrameProjection Projection,
    ReplayTimelineEntry Timeline,
    IReadOnlyList<ReplayContact> ReportedContacts,
    IReadOnlyList<ReplayContact> HostContacts,
    bool GlobalPalm,
    bool HostStateUpdated) : ITouchReplaySnapshot
{
    public object DecodedFrame => Frame!;
    public SourceRecord PrimarySource => Projection.PrimarySource;
    public IReadOnlyList<SourceRecord> PhysicalRecords => Projection.PhysicalRecords;
    public bool AllBreak => Projection.AllBreak;
}

public interface ITouchReplaySession
{
    int Count { get; }
    int CheckpointCount => 0;
    TimeSpan FrameInterval { get; }
    IReadOnlyList<ReplayTimelineEntry> Timeline { get; }
    IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
    IReadOnlyList<ReplayContact> AllReportedContacts { get; }
    ITouchReplaySnapshot Seek(int logicalIndex);
}

public class TouchReplaySession<TFrame> : ITouchReplaySession
{
    private readonly IReadOnlyList<TFrame> frames;
    private readonly IReadOnlyList<TouchReplayFrameProjection> projections;
    private readonly IReadOnlyList<ReplayTimelineEntry> timeline;
    private readonly SortedDictionary<int, HostCheckpoint> checkpoints = [];
    private readonly TouchReplayOptions options;

    protected TouchReplaySession(
        IReadOnlyList<TFrame> frames,
        Func<TFrame, TouchReplayFrameProjection> projector,
        TouchReplayOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(projector);
        this.frames = frames;
        this.options = options ?? TouchReplayOptions.Default;
        ValidateOptions(this.options);
        projections = frames.Select(projector).ToArray();
        AllReportedContacts = projections.SelectMany(item => item.ReportedContacts).ToArray();
        FrameInterval = this.options.DefaultFrameInterval;
        timeline = BuildTimeline(projections, FrameInterval, this.options);
        Diagnostics = BuildCheckpointsAndDiagnostics();
    }

    public int Count => frames.Count;
    public int CheckpointCount => checkpoints.Count;
    public TimeSpan FrameInterval { get; }
    public IReadOnlyList<ReplayTimelineEntry> Timeline => timeline;
    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
    public IReadOnlyList<ReplayContact> AllReportedContacts { get; }

    public TouchReplaySnapshot<TFrame> Seek(int logicalIndex)
    {
        if (logicalIndex < 0 || logicalIndex >= frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        }

        var checkpointPair = checkpoints.Last(pair => pair.Key <= logicalIndex);
        var state = checkpointPair.Value;
        for (var index = checkpointPair.Key + 1; index <= logicalIndex; index++)
        {
            state = Apply(state, projections[index]);
        }

        var projection = projections[logicalIndex];
        return new TouchReplaySnapshot<TFrame>(
            logicalIndex,
            frames[logicalIndex],
            projection,
            timeline[logicalIndex],
            projection.ReportedContacts,
            state.Contacts.Values.OrderBy(contact => contact.Id).ToArray(),
            state.GlobalPalm,
            projection.HostStateEligible);
    }

    ITouchReplaySnapshot ITouchReplaySession.Seek(int logicalIndex) => Seek(logicalIndex);

    public TouchReplaySnapshot<TFrame> Step(int logicalIndex, int delta)
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
            state = Apply(state, projections[index]);
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
        var source = projections[^1].PrimarySource;
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

    private static HostCheckpoint Apply(HostCheckpoint prior, TouchReplayFrameProjection frame)
    {
        if (!frame.HostStateEligible || frame.HostReportedContacts.GroupBy(contact => contact.Id).Any(group => group.Count() > 1))
        {
            return prior;
        }
        if (frame.AllBreak || frame.GlobalPalm)
        {
            return new HostCheckpoint(new Dictionary<byte, ReplayContact>(), frame.GlobalPalm);
        }
        return new HostCheckpoint(
            frame.HostReportedContacts.GroupBy(contact => contact.Id).ToDictionary(group => group.Key, group => group.Last()),
            frame.GlobalPalm);
    }

    private static IReadOnlyList<ReplayTimelineEntry> BuildTimeline(
        IReadOnlyList<TouchReplayFrameProjection> frames,
        TimeSpan frameInterval,
        TouchReplayOptions options)
    {
        var entries = new ReplayTimelineEntry[frames.Count];
        var recorded = TimeSpan.Zero;
        var display = TimeSpan.Zero;
        DateTimeOffset? previousTimestamp = null;
        for (var index = 0; index < frames.Count; index++)
        {
            var timestamp = frames[index].PrimarySource.Timestamp;
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

    private static void ValidateOptions(TouchReplayOptions options)
    {
        if (options.CheckpointInterval <= 0 ||
            options.DefaultFrameInterval <= TimeSpan.Zero ||
            options.IdleGapThreshold <= TimeSpan.Zero ||
            options.CompressedIdleGap <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Replay intervals and checkpoint size must be positive.");
        }
    }

    private sealed record HostCheckpoint(IReadOnlyDictionary<byte, ReplayContact> Contacts, bool GlobalPalm)
    {
        public static HostCheckpoint Empty { get; } = new(new Dictionary<byte, ReplayContact>(), false);
    }
}
