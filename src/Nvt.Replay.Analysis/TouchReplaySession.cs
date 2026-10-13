using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

/// <summary>Configures host-state checkpoints, nominal frame timing, and display-time compression of idle gaps.</summary>
/// <param name="CheckpointInterval">The number of logical frames between cached host-state checkpoints.</param>
/// <param name="DefaultFrameInterval">The nominal frame interval used for frame time and unusable timestamp gaps.</param>
/// <param name="IdleGapThreshold">The recorded interval above which a gap is replaced in display time.</param>
/// <param name="CompressedIdleGap">The display-time interval substituted for gaps above the idle gap threshold.</param>
public sealed record TouchReplayOptions(
    int CheckpointInterval,
    TimeSpan DefaultFrameInterval,
    TimeSpan IdleGapThreshold,
    TimeSpan CompressedIdleGap)
{
    /// <summary>Gets the nominal frame interval calculated as one second divided by 120.</summary>
    public static TimeSpan NominalTpFrameInterval { get; } =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 120);

    /// <summary>Gets replay options with 256-frame checkpoints, 120 Hz timing, and gaps over one second replaced by 100 ms.</summary>
    public static TouchReplayOptions Default { get; } = new(
        256,
        NominalTpFrameInterval,
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(100));
}

/// <summary>Stores a reported contact's identifier, type, status, coordinates, source record, and invalid flag.</summary>
/// <param name="Id">The identifier of the reported contact.</param>
/// <param name="Type">The touch type reported for the contact.</param>
/// <param name="Status">The touch status reported for the contact.</param>
/// <param name="X">The X coordinate stored for the reported contact.</param>
/// <param name="Y">The Y coordinate stored for the reported contact.</param>
/// <param name="SourceRecordId">The identifier of the source record that reported this contact.</param>
/// <param name="Invalid">Whether the contact is marked invalid and excluded from active-contact checks.</param>
public sealed record ReplayContact(
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    string SourceRecordId,
    bool Invalid = false)
{
    /// <summary>Indicates an Enter or Move contact, or a Palm contact with NoFinger status, unless marked invalid.</summary>
    public bool IsActive =>
        !Invalid &&
        (Status is TouchStatus.Enter or TouchStatus.Move ||
         Type == TouchType.Palm && Status == TouchStatus.NoFinger);
}

/// <summary>Stores recorded, nominal frame, and compressed display times for a logical replay frame.</summary>
/// <param name="LogicalIndex">The zero-based logical frame index.</param>
/// <param name="RecordedTime">The elapsed capture time accumulated from timestamps or synthetic frame intervals.</param>
/// <param name="FrameTime">The logical frame index multiplied by the nominal frame interval.</param>
/// <param name="DisplayTime">The elapsed display time after replacing gaps above the idle gap threshold.</param>
/// <param name="RecordedDelta">The interval added to recorded time for this frame, with zero used for the first frame.</param>
/// <param name="TimestampSynthetic">Whether the frame lacks usable timestamp timing and uses synthetic timing.</param>
/// <param name="IdleGapCompressed">Whether the recorded interval exceeds the configured idle gap threshold.</param>
public sealed record ReplayTimelineEntry(
    int LogicalIndex,
    TimeSpan RecordedTime,
    TimeSpan FrameTime,
    TimeSpan DisplayTime,
    TimeSpan RecordedDelta,
    bool TimestampSynthetic,
    bool IdleGapCompressed);

/// <summary>Carries source evidence, reported contacts, and host-state flags projected from a decoded frame.</summary>
/// <param name="PrimarySource">The source record used for frame timestamps and diagnostic provenance.</param>
/// <param name="PhysicalRecords">The physical source records associated with the decoded frame.</param>
/// <param name="ReportedContacts">The contacts reported by the frame.</param>
/// <param name="HostReportedContacts">The contacts used to replace host state when the frame is eligible.</param>
/// <param name="AllBreak">Whether the frame carries an All Break flag that clears eligible host contact state.</param>
/// <param name="GlobalPalm">Whether the frame carries a global palm flag that clears eligible host contact state.</param>
/// <param name="HostStateEligible">Whether the frame is eligible to update host contact state.</param>
public sealed record TouchReplayFrameProjection(
    SourceRecord PrimarySource,
    IReadOnlyList<SourceRecord> PhysicalRecords,
    IReadOnlyList<ReplayContact> ReportedContacts,
    IReadOnlyList<ReplayContact> HostReportedContacts,
    bool AllBreak,
    bool GlobalPalm,
    bool HostStateEligible);

/// <summary>Exposes a decoded frame, its source evidence and timing, and its reported and replayed host contact states.</summary>
public interface ITouchReplaySnapshot
{
    /// <summary>Gets the zero-based logical frame index of the snapshot.</summary>
    int LogicalIndex { get; }
    /// <summary>Gets the decoded frame represented by the snapshot.</summary>
    object DecodedFrame { get; }
    /// <summary>Gets the source record selected as the frame's primary evidence.</summary>
    SourceRecord PrimarySource { get; }
    /// <summary>Gets the physical source records associated with the decoded frame.</summary>
    IReadOnlyList<SourceRecord> PhysicalRecords { get; }
    /// <summary>Gets the recorded, nominal frame, and compressed display times for the snapshot.</summary>
    ReplayTimelineEntry Timeline { get; }
    /// <summary>Gets the contacts reported by the snapshot's frame.</summary>
    IReadOnlyList<ReplayContact> ReportedContacts { get; }
    /// <summary>Gets the host contact state reconstructed through this frame.</summary>
    IReadOnlyList<ReplayContact> HostContacts { get; }
    /// <summary>Indicates an All Break frame; the default implementation returns false.</summary>
    bool AllBreak => false;
    /// <summary>Indicates whether the replayed host state has its global palm flag set.</summary>
    bool GlobalPalm { get; }
    /// <summary>Indicates whether the frame is marked as eligible to update host state.</summary>
    bool HostStateUpdated { get; }
}

/// <summary>Stores a typed decoded frame with its source projection, timing, reported contacts, and replayed host state.</summary>
/// <param name="LogicalIndex">The zero-based logical index of the decoded frame.</param>
/// <param name="Frame">The decoded frame represented by the snapshot.</param>
/// <param name="Projection">The source evidence, reported contacts, and host-state flags projected from the frame.</param>
/// <param name="Timeline">The recorded, nominal frame, and compressed display times for the frame.</param>
/// <param name="ReportedContacts">The contacts reported by this frame's projection.</param>
/// <param name="HostContacts">The host contact state after applying eligible frame projections.</param>
/// <param name="GlobalPalm">Whether the replayed host state's global palm flag is set.</param>
/// <param name="HostStateUpdated">Whether the frame projection is marked as eligible to update host state.</param>
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
    /// <inheritdoc />
    public object DecodedFrame => Frame!;
    /// <inheritdoc />
    public SourceRecord PrimarySource => Projection.PrimarySource;
    /// <inheritdoc />
    public IReadOnlyList<SourceRecord> PhysicalRecords => Projection.PhysicalRecords;
    /// <inheritdoc />
    public bool AllBreak => Projection.AllBreak;
}

/// <summary>Exposes replay frames, timeline evidence, diagnostics, and snapshot reconstruction.</summary>
public interface ITouchReplaySession
{
    /// <summary>Gets the number of decoded frames in the replay session.</summary>
    int Count { get; }
    /// <summary>Gets the number of cached host-state checkpoints; the default implementation returns zero.</summary>
    int CheckpointCount => 0;
    /// <summary>Gets the configured nominal interval used for frame time and synthetic timestamp gaps.</summary>
    TimeSpan FrameInterval { get; }
    /// <summary>Gets the recorded, nominal frame, and compressed display times for each logical frame.</summary>
    IReadOnlyList<ReplayTimelineEntry> Timeline { get; }
    /// <summary>Gets the diagnostics associated with the replay session.</summary>
    IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
    /// <summary>Gets all contacts reported by frame projections in capture order.</summary>
    IReadOnlyList<ReplayContact> AllReportedContacts { get; }
    /// <summary>Reconstructs the replay snapshot at the requested logical frame index.</summary>
    ITouchReplaySnapshot Seek(int logicalIndex);

    /// <summary>Enumerates replay snapshots in logical frame order and checks cancellation before each frame.</summary>
    IEnumerable<ITouchReplaySnapshot> EnumerateSnapshots(CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Seek(index);
        }
    }
}

/// <summary>Reconstructs replay snapshots and host contact state from frame projections using cached checkpoints.</summary>
public class TouchReplaySession<TFrame> : ITouchReplaySession
{
    private readonly IReadOnlyList<TFrame> frames;
    private readonly IReadOnlyList<TouchReplayFrameProjection> projections;
    private readonly IReadOnlyList<ReplayTimelineEntry> timeline;
    private readonly SortedDictionary<int, HostCheckpoint> checkpoints = [];
    private readonly TouchReplayOptions options;

    /// <summary>Creates frame projections, replay timelines, host-state checkpoints, and capture-end diagnostics.</summary>
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

    /// <inheritdoc />
    public int Count => frames.Count;
    /// <inheritdoc />
    public int CheckpointCount => checkpoints.Count;
    /// <inheritdoc />
    public TimeSpan FrameInterval { get; }
    /// <inheritdoc />
    public IReadOnlyList<ReplayTimelineEntry> Timeline => timeline;
    /// <inheritdoc />
    public IReadOnlyList<ReplayDiagnostic> Diagnostics { get; }
    /// <inheritdoc />
    public IReadOnlyList<ReplayContact> AllReportedContacts { get; }

    /// <summary>Reconstructs a typed snapshot at the requested logical frame index from the nearest cached checkpoint.</summary>
    public TouchReplaySnapshot<TFrame> Seek(int logicalIndex)
    {
        if (logicalIndex < 0 || logicalIndex >= frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(logicalIndex));
        }

        var checkpointIndex = checkpoints.ContainsKey(logicalIndex)
            ? logicalIndex
            : logicalIndex - (logicalIndex % options.CheckpointInterval);
        var state = checkpoints[checkpointIndex];
        for (var index = checkpointIndex + 1; index <= logicalIndex; index++)
        {
            state = Apply(state, projections[index]);
        }

        return Snapshot(logicalIndex, state);
    }

    ITouchReplaySnapshot ITouchReplaySession.Seek(int logicalIndex) => Seek(logicalIndex);

    /// <inheritdoc />
    public IEnumerable<ITouchReplaySnapshot> EnumerateSnapshots(CancellationToken cancellationToken = default)
    {
        var state = HostCheckpoint.Empty;
        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state = Apply(state, projections[index]);
            yield return Snapshot(index, state);
        }
    }

    /// <summary>Returns the snapshot at the supplied logical index plus the delta, clamped to the loaded frame range.</summary>
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

    private TouchReplaySnapshot<TFrame> Snapshot(int logicalIndex, HostCheckpoint state)
    {
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
