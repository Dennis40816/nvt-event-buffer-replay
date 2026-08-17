using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Tests;

public sealed class CommonEventBufferDecoderTests
{
    [Fact]
    public void Crc_matches_Python_oracle_vector()
    {
        Assert.Equal(0x37, CommonCrc8.Compute("123456789"u8));
    }

    [Fact]
    public void Decodes_known_0x83_finger_coordinates()
    {
        var packet = Convert.FromHexString(
            "05" +
            "12088B0452FFFF" +
            "22097A0388FFFF" +
            "32067A01BFFFFF" +
            "4207BF047DFFFF" +
            "5206B103D8FFFF" +
            new string('0', 70) +
            new string('0', 18));
        packet[79] = CommonCrc8.Compute(packet.AsSpan(0, 79));

        var result = Decode(packet, CommonEventBufferVersion.V83);

        Assert.NotNull(result.Frame);
        Assert.Equal(5, result.Frame.NumTouches);
        Assert.Collection(
            result.Frame.Fingers.Take(5),
            finger => AssertFinger(finger, 1, 2187, 1106),
            finger => AssertFinger(finger, 2, 2426, 904),
            finger => AssertFinger(finger, 3, 1658, 447),
            finger => AssertFinger(finger, 4, 1983, 1149),
            finger => AssertFinger(finger, 5, 1713, 984));
        Assert.True(result.Frame.CrcValid);
        Assert.True(result.Frame.HostStateEligible);
    }

    [Fact]
    public void Decodes_0x82_tail_without_inventing_knob_meaning()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V82);
        packet[71] = 0xF8;
        packet[72] = 0x12;
        packet[73] = 0x1A;
        packet[74] = 0x31;
        packet[75] = 0xFB;
        Seal(packet);

        var frame = Assert.IsType<CommonEventBufferFrame>(Decode(packet, CommonEventBufferVersion.V82).Frame);

        Assert.True(frame.AllBreak);
        Assert.True(frame.Button.Valid);
        Assert.Equal(AsilErrorCode.RuntimeFail, frame.Asil.ErrorCode);
        Assert.True(frame.Asil.Short);
        Assert.True(frame.Asil.Open);
        Assert.True(frame.Asil.ResumeFlagDeprecated);
        Assert.Equal(new CommonKnob(0x31, 0xFB, "todo"), frame.Knob);
    }

    [Fact]
    public void Decodes_0x84_bus_counters()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V84);
        packet[78] = 0xA5;
        Seal(packet);

        var bus = Assert.IsType<CommonBusStatus>(Decode(packet, CommonEventBufferVersion.V84).Frame?.BusStatus);

        Assert.Equal(0xA, bus.NoResponseCounter);
        Assert.Equal(0x5, bus.BusyCounter);
        Assert.Equal(0xF, bus.CounterMaximum);
        Assert.Equal("clamp_expected", bus.CounterBehavior);
    }

    [Fact]
    public void Decodes_0x85_EMS_as_bitmap_and_global_palm()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V85);
        packet[75] = 0x15;
        Seal(packet);

        var diagnostic = Assert.IsType<CommonDiagnosticPacket>(Decode(packet, CommonEventBufferVersion.V85).Frame?.DiagnosticPacket);

        Assert.Equal(0xA, diagnostic.EmsBitmap);
        Assert.Equal([0, 1, 0, 1], diagnostic.EmsBitsLsbFirst);
        Assert.True(diagnostic.PalmOn);
    }

    [Fact]
    public void Crc_failure_is_inspectable_but_not_host_state_eligible()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V83);
        packet[79] ^= 0xFF;

        var result = Decode(packet, CommonEventBufferVersion.V83);

        Assert.NotNull(result.Frame);
        Assert.False(result.Frame.CrcValid);
        Assert.False(result.Frame.HostStateEligible);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "COMMON_CRC_MISMATCH");
    }

    [Fact]
    public void Short_packet_returns_length_error_without_repair()
    {
        var result = Decode(new byte[79], CommonEventBufferVersion.V83);

        Assert.Null(result.Frame);
        Assert.Equal("FORMAT_LENGTH_MISMATCH", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void Trailing_bytes_are_preserved()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V83).Concat(new byte[] { 0xAA, 0xBB }).ToArray();

        var frame = Assert.IsType<CommonEventBufferFrame>(Decode(packet, CommonEventBufferVersion.V83).Frame);

        Assert.Equal([0xAA, 0xBB], frame.UnconsumedData);
    }

    [Fact]
    public void Touch_count_above_public_maximum_is_warned_and_not_host_eligible()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V83);
        packet[0] = 0x0B;
        Seal(packet);

        var result = Decode(packet, CommonEventBufferVersion.V83);

        Assert.False(result.Frame?.HostStateEligible);
        Assert.Contains(result.Diagnostics, item => item.Code == "COMMON_TOUCH_COUNT_OUT_OF_RANGE");
    }

    [Fact]
    public void Duplicate_contact_ids_remain_decodable_but_cannot_update_host_state()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V83);
        packet[0] = 0x02;
        WriteFinger(packet, 0, id: 3, TouchStatus.Enter, x: 100, y: 200);
        WriteFinger(packet, 1, id: 3, TouchStatus.Move, x: 300, y: 400);
        Seal(packet);

        var result = Decode(packet, CommonEventBufferVersion.V83);

        Assert.NotNull(result.Frame);
        Assert.False(result.Frame.HostStateEligible);
        Assert.Equal(2, result.Frame.Fingers.Count(finger => finger.IsReported));
        Assert.Contains(result.Diagnostics, item => item.Code == "COMMON_DUPLICATE_CONTACT_ID");
    }

    [Fact]
    public void Unambiguous_active_touch_count_mismatch_cannot_update_host_state()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V83);
        packet[0] = 0x02;
        WriteFinger(packet, 0, id: 1, TouchStatus.Move, x: 100, y: 200);
        Seal(packet);

        var result = Decode(packet, CommonEventBufferVersion.V83);

        Assert.False(result.Frame?.HostStateEligible);
        Assert.Contains(result.Diagnostics, item => item.Code == "COMMON_ACTIVE_TOUCH_COUNT_MISMATCH");
    }

    [Fact]
    public void Break_evidence_does_not_guess_project_specific_touch_count_semantics()
    {
        var packet = NewAllBreak(CommonEventBufferVersion.V83);
        packet[0] = 0x01;
        WriteFinger(packet, 0, id: 1, TouchStatus.Break, x: 100, y: 200);
        Seal(packet);

        var result = Decode(packet, CommonEventBufferVersion.V83);

        Assert.True(result.Frame?.HostStateEligible);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code == "COMMON_ACTIVE_TOUCH_COUNT_MISMATCH");
    }

    internal static byte[] NewAllBreak(CommonEventBufferVersion version)
    {
        var packet = new byte[CommonEventBufferDecoder.FrameLength];
        packet.AsSpan(1, CommonEventBufferDecoder.FingerCount * CommonEventBufferDecoder.FingerLength).Fill(0xFF);
        var buttonLength = version is CommonEventBufferVersion.V82 or CommonEventBufferVersion.V83 ? 2 : 3;
        packet.AsSpan(71, buttonLength).Fill(0xFF);
        Seal(packet);
        return packet;
    }

    internal static void Seal(byte[] packet)
    {
        packet[79] = CommonCrc8.Compute(packet.AsSpan(0, 79));
    }

    private static void WriteFinger(byte[] packet, int slot, byte id, TouchStatus status, ushort x, ushort y)
    {
        var offset = 1 + (slot * CommonEventBufferDecoder.FingerLength);
        packet[offset] = (byte)((id << 4) | (byte)status);
        packet[offset + 1] = (byte)(x >> 8);
        packet[offset + 2] = (byte)x;
        packet[offset + 3] = (byte)(y >> 8);
        packet[offset + 4] = (byte)y;
        packet[offset + 5] = 0xFF;
        packet[offset + 6] = 0xFF;
    }

    internal static SourceRecord Source(byte[] packet, long index = 1) =>
        new(
            index,
            $"fixture:L{index}",
            DateTimeOffset.Parse("2026-08-14T10:00:00+08:00", System.Globalization.CultureInfo.InvariantCulture),
            BusOperation.Read,
            "TP",
            0x01,
            packet.Length,
            packet,
            "synthetic",
            new SourceLocation(index * 100, (int)index));

    private static CommonDecodeResult Decode(byte[] packet, CommonEventBufferVersion version) =>
        new CommonEventBufferDecoder().Decode(Source(packet), version);

    private static void AssertFinger(CommonFinger finger, byte id, ushort x, ushort y)
    {
        Assert.Equal(id, finger.Id);
        Assert.Equal(TouchType.Finger, finger.Type);
        Assert.Equal(TouchStatus.Move, finger.Status);
        Assert.Equal(x, finger.X);
        Assert.Equal(y, finger.Y);
    }
}
