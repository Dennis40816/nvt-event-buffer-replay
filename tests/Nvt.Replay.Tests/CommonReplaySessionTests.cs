using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Tests;

public sealed class CommonReplaySessionTests
{
    [Fact]
    public void Seeking_backward_matches_forward_reduction()
    {
        var frames = new[]
        {
            FingerFrame(1, TouchStatus.Enter, 100, 200, 0),
            FingerFrame(1, TouchStatus.Move, 120, 220, 1),
            FingerFrame(1, TouchStatus.Break, 120, 220, 2),
            AllBreakFrame(3),
        };
        var replay = new CommonReplaySession(frames, Options(checkpointInterval: 2));

        var forward = Enumerable.Range(0, replay.Count).Select(replay.Seek).ToArray();
        var backward = Enumerable.Range(0, replay.Count).Reverse().Select(replay.Seek).Reverse().ToArray();

        for (var index = 0; index < forward.Length; index++)
        {
            Assert.Equal(forward[index].LogicalIndex, backward[index].LogicalIndex);
            Assert.Equal(forward[index].Timeline, backward[index].Timeline);
            Assert.Equal(forward[index].ReportedContacts, backward[index].ReportedContacts);
            Assert.Equal(forward[index].HostContacts, backward[index].HostContacts);
            Assert.Equal(forward[index].GlobalPalm, backward[index].GlobalPalm);
        }
        Assert.Equal(TouchStatus.Enter, Assert.Single(forward[0].HostContacts).Status);
        Assert.Equal(TouchStatus.Move, Assert.Single(forward[1].HostContacts).Status);
        Assert.Equal(TouchStatus.Break, Assert.Single(forward[2].HostContacts).Status);
        Assert.Empty(forward[3].HostContacts);
    }

    [Fact]
    public void Invalid_CRC_leaves_prior_host_state_unchanged()
    {
        var entered = FingerFrame(1, TouchStatus.Enter, 100, 200, 0);
        var invalidPacket = CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83);
        invalidPacket[79] ^= 0xFF;
        var invalid = Decode(invalidPacket, 1, Milliseconds(1));
        var replay = new CommonReplaySession([entered, invalid]);

        var snapshot = replay.Seek(1);

        Assert.False(snapshot.HostStateUpdated);
        Assert.Equal(entered.Source.StableId, Assert.Single(snapshot.HostContacts).SourceRecordId);
        Assert.Empty(snapshot.ReportedContacts);
    }

    [Fact]
    public void Global_palm_suppresses_host_contacts_but_preserves_reported_evidence()
    {
        var packet = FingerPacket(CommonEventBufferVersion.V85, 2, TouchType.Palm, TouchStatus.Enter, 300, 400);
        packet[75] |= 0x01;
        CommonEventBufferDecoderTests.Seal(packet);
        var replay = new CommonReplaySession([Decode(packet, 0, Milliseconds(0), CommonEventBufferVersion.V85)]);

        var snapshot = replay.Seek(0);

        Assert.True(snapshot.GlobalPalm);
        Assert.Empty(snapshot.HostContacts);
        Assert.Equal(TouchType.Palm, Assert.Single(snapshot.ReportedContacts).Type);
    }

    [Fact]
    public void Capture_end_with_active_contact_warns_without_inventing_all_break()
    {
        var replay = new CommonReplaySession([FingerFrame(3, TouchStatus.Move, 500, 600, 0)]);

        var diagnostic = Assert.Single(replay.Diagnostics);

        Assert.Equal("CAPTURE_END_ACTIVE_CONTACTS", diagnostic.Code);
        Assert.Single(replay.Seek(0).HostContacts);
        Assert.False(replay.Seek(0).Frame.AllBreak);
    }

    [Fact]
    public void Recorded_frame_and_compressed_display_clocks_remain_distinct()
    {
        var frames = new[]
        {
            AllBreakFrame(0, Milliseconds(0)),
            AllBreakFrame(1, Milliseconds(10)),
            AllBreakFrame(2, Milliseconds(5000)),
            MissingTimestampFrame(3),
        };
        var replay = new CommonReplaySession(frames, Options(checkpointInterval: 2));

        Assert.Equal(Milliseconds(10), replay.FrameInterval);
        Assert.Equal(Milliseconds(4990), replay.Timeline[2].RecordedDelta);
        Assert.True(replay.Timeline[2].IdleGapCompressed);
        Assert.Equal(Milliseconds(110), replay.Timeline[2].DisplayTime);
        Assert.True(replay.Timeline[3].TimestampSynthetic);
        Assert.Equal(Milliseconds(30), replay.Timeline[3].FrameTime);
    }

    [Fact]
    public void Palm_with_no_finger_status_is_still_reported_when_not_global_palm()
    {
        var packet = FingerPacket(CommonEventBufferVersion.V83, 4, TouchType.Palm, TouchStatus.NoFinger, 700, 800);
        var replay = new CommonReplaySession([Decode(packet, 0, Milliseconds(0))]);

        var contact = Assert.Single(replay.Seek(0).HostContacts);

        Assert.Equal(TouchType.Palm, contact.Type);
        Assert.True(contact.IsActive);
    }

    [Fact]
    public void Palm_break_is_not_treated_as_active_at_capture_end()
    {
        var packet = FingerPacket(CommonEventBufferVersion.V83, 4, TouchType.Palm, TouchStatus.Break, 700, 800);
        var replay = new CommonReplaySession([Decode(packet, 0, Milliseconds(0))]);

        Assert.Equal(TouchStatus.Break, Assert.Single(replay.Seek(0).HostContacts).Status);
        Assert.Empty(replay.Diagnostics);
    }

    [Fact]
    public void Sparse_checkpoints_reconstruct_large_capture_in_either_direction()
    {
        const int frameCount = 50_000;
        var template = AllBreakFrame(0);
        var frames = Enumerable.Range(0, frameCount)
            .Select(index => template with
            {
                Source = template.Source with
                {
                    Index = index,
                    Timestamp = template.Source.Timestamp + Milliseconds(index * 10),
                },
            })
            .ToArray();
        var replay = new CommonReplaySession(frames, Options(checkpointInterval: 256));

        foreach (var index in new[] { frameCount - 1, 32_768, 12_345, 256, 1, 0 })
        {
            var snapshot = replay.Seek(index);
            Assert.Equal(index, snapshot.LogicalIndex);
            Assert.Empty(snapshot.HostContacts);
        }
    }

    private static CommonEventBufferFrame FingerFrame(
        byte id,
        TouchStatus status,
        ushort x,
        ushort y,
        int index) =>
        Decode(FingerPacket(CommonEventBufferVersion.V83, id, TouchType.Finger, status, x, y), index, Milliseconds(index * 10));

    private static CommonEventBufferFrame AllBreakFrame(int index) =>
        Decode(
            CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83),
            index,
            Milliseconds(index * 10));

    private static CommonEventBufferFrame AllBreakFrame(int index, TimeSpan timestamp) =>
        Decode(
            CommonEventBufferDecoderTests.NewAllBreak(CommonEventBufferVersion.V83),
            index,
            timestamp);

    private static CommonEventBufferFrame MissingTimestampFrame(int index)
    {
        var frame = AllBreakFrame(index);
        return frame with { Source = frame.Source with { Timestamp = null } };
    }

    private static byte[] FingerPacket(
        CommonEventBufferVersion version,
        byte id,
        TouchType type,
        TouchStatus status,
        ushort x,
        ushort y)
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(version);
        packet[0] = 0x01;
        packet[1] = (byte)((id << 4) | ((byte)type << 2) | (byte)status);
        packet[2] = (byte)(x >> 8);
        packet[3] = (byte)x;
        packet[4] = (byte)(y >> 8);
        packet[5] = (byte)y;
        packet[6] = 0xFF;
        packet[7] = 0xFF;
        CommonEventBufferDecoderTests.Seal(packet);
        return packet;
    }

    private static CommonEventBufferFrame Decode(
        byte[] packet,
        int index,
        TimeSpan timestamp,
        CommonEventBufferVersion version = CommonEventBufferVersion.V83)
    {
        var source = CommonEventBufferDecoderTests.Source(packet, index) with
        {
            Timestamp = DateTimeOffset.Parse("2026-08-14T10:00:00+08:00") + timestamp,
        };
        return Assert.IsType<CommonEventBufferFrame>(new CommonEventBufferDecoder().Decode(source, version).Frame);
    }

    private static TouchReplayOptions Options(int checkpointInterval) => new(
        checkpointInterval,
        Milliseconds(10),
        TimeSpan.FromSeconds(1),
        Milliseconds(100));

    private static TimeSpan Milliseconds(double value) => TimeSpan.FromMilliseconds(value);
}
