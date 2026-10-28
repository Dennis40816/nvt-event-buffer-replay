using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Tests;

public sealed class A5FormatsCharacterizationTests
{
    // docs/common-event-buffer.md, "Shared 80-byte layout" and "CRC and invalid data".
    [Theory]
    [InlineData(CommonEventBufferVersion.V82, 0)]
    [InlineData(CommonEventBufferVersion.V82, 79)]
    [InlineData(CommonEventBufferVersion.V83, 0)]
    [InlineData(CommonEventBufferVersion.V83, 79)]
    [InlineData(CommonEventBufferVersion.V84, 0)]
    [InlineData(CommonEventBufferVersion.V84, 79)]
    [InlineData(CommonEventBufferVersion.V85, 0)]
    [InlineData(CommonEventBufferVersion.V85, 79)]
    public void Common_short_frames_keep_bytes_and_report_structured_length(
        CommonEventBufferVersion version, int length)
    {
        var bytes = Enumerable.Repeat((byte)0xA5, length).ToArray();
        var source = CommonEventBufferDecoderTests.Source(bytes);

        var result = new CommonEventBufferDecoder().Decode(source, version);

        Assert.Null(result.Frame);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("FORMAT_LENGTH_MISMATCH", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("80", diagnostic.Details?["expected"]);
        Assert.Equal(length.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnostic.Details?["actual"]);
        Assert.Equal(source.StableId, diagnostic.SourceRecordId);
        Assert.Equal(source.Location, diagnostic.Location);
        Assert.Equal(Enumerable.Repeat((byte)0xA5, length), source.Data);
    }

    // docs/common-event-buffer.md, "Version tails": offsets are selected by the operator's version.
    [Theory]
    [InlineData(CommonEventBufferVersion.V82, "F812", 0x34, "CDABFF")]
    [InlineData(CommonEventBufferVersion.V83, "F812", 0x34, "E781CDABFF")]
    [InlineData(CommonEventBufferVersion.V84, "F81234", 0xE7, "81CDAB")]
    [InlineData(CommonEventBufferVersion.V85, "F81234", 0xE7, "CDAB")]
    public void Common_exact_80_bytes_select_version_tail_and_preserve_reserved_fields(
        CommonEventBufferVersion version, string buttonHex, byte asil, string reservedHex)
    {
        var bytes = CommonEventBufferDecoderTests.NewAllBreak(version);
        Convert.FromHexString("F81234E781CDABFF").CopyTo(bytes, 71);
        CommonEventBufferDecoderTests.Seal(bytes);
        var original = bytes.ToArray();
        var source = CommonEventBufferDecoderTests.Source(bytes);

        var result = new CommonEventBufferDecoder().Decode(source, version);
        var frame = Assert.IsType<CommonEventBufferFrame>(result.Frame);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(version, frame.Version);
        Assert.True(frame.AllBreak);
        Assert.True(frame.HostStateEligible);
        Assert.Equal(10, frame.Fingers.Count);
        Assert.All(frame.Fingers, finger =>
        {
            Assert.True(finger.IsUnused);
            Assert.False(finger.IsReported);
        });
        Assert.Equal(Convert.FromHexString(buttonHex), frame.Button.Raw);
        Assert.True(frame.Button.Valid);
        Assert.Equal("project_defined", frame.Button.Mapping);
        Assert.Equal(asil, frame.Asil.Raw);
        Assert.Equal(Convert.FromHexString(reservedHex), frame.ReservedTail);
        Assert.Empty(frame.UnconsumedData);
        Assert.Equal(original[79], frame.CapturedCrc);
        Assert.True(frame.CrcValid);
        Assert.Same(source, frame.Source);
        Assert.Equal(original, source.Data);

        if (version == CommonEventBufferVersion.V82)
        {
            Assert.Equal(new CommonKnob(0xE7, 0x81, "todo"), frame.Knob);
            Assert.True(frame.Asil.ResumeFlagDeprecated);
            Assert.Equal(1, frame.Asil.UpperBits);
        }
        else
        {
            Assert.Null(frame.Knob);
            Assert.False(frame.Asil.ResumeFlagDeprecated);
        }

        if (version is CommonEventBufferVersion.V84 or CommonEventBufferVersion.V85)
        {
            var bus = Assert.IsType<CommonBusStatus>(frame.BusStatus);
            Assert.Equal(0xFF, bus.Raw);
            Assert.Equal(15, bus.NoResponseCounter);
            Assert.Equal(15, bus.BusyCounter);
        }
        else
        {
            Assert.Null(frame.BusStatus);
        }

        if (version == CommonEventBufferVersion.V85)
        {
            var diagnostic = Assert.IsType<CommonDiagnosticPacket>(frame.DiagnosticPacket);
            Assert.Equal(0x81, diagnostic.Raw);
            Assert.Equal(4, diagnostic.Reserved);
            Assert.Equal(0, diagnostic.EmsBitmap);
            Assert.Equal(new byte[] { 0, 0, 0, 0 }, diagnostic.EmsBitsLsbFirst);
            Assert.True(diagnostic.PalmOn);
        }
        else
        {
            Assert.Null(frame.DiagnosticPacket);
        }
    }

    // docs/common-event-buffer.md, "Shared 80-byte layout" and "CRC and invalid data":
    // big-endian coordinates, raw metadata/reserved bytes, and trailing unconsumed evidence.
    [Theory]
    [InlineData(CommonEventBufferVersion.V82)]
    [InlineData(CommonEventBufferVersion.V83)]
    [InlineData(CommonEventBufferVersion.V84)]
    [InlineData(CommonEventBufferVersion.V85)]
    public void Common_trailing_bytes_do_not_enter_crc_or_change_raw_finger_evidence(CommonEventBufferVersion version)
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(version);
        packet[0] = 0xE1;
        Convert.FromHexString("7E0100FFFF00AA").CopyTo(packet, 1);
        CommonEventBufferDecoderTests.Seal(packet);
        byte[] trailing = [0xAA, 0xBB, 0x00, 0xFF];
        byte[] bytes = [.. packet, .. trailing];
        var original = bytes.ToArray();
        var source = CommonEventBufferDecoderTests.Source(bytes);

        var result = new CommonEventBufferDecoder().Decode(source, version);
        var frame = Assert.IsType<CommonEventBufferFrame>(result.Frame);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(0xE1, frame.RawHeader);
        Assert.Equal(7, frame.ReservedHeader);
        Assert.Equal(1, frame.NumTouches);
        Assert.False(frame.TpAsilError);
        var finger = Assert.Single(frame.Fingers, finger => finger.IsReported);
        Assert.Equal(0x7E, finger.RawMeta);
        Assert.Equal(7, finger.Id);
        Assert.Equal(TouchType.Reserved, finger.Type);
        Assert.Equal(TouchStatus.Move, finger.Status);
        Assert.Equal((ushort)0x0100, finger.X);
        Assert.Equal(ushort.MaxValue, finger.Y);
        Assert.Equal(new byte[] { 0x00, 0xAA }, finger.Reserved);
        Assert.Equal(trailing, frame.UnconsumedData);
        Assert.Equal(packet[79], frame.CapturedCrc);
        Assert.Equal(packet[79], frame.ComputedCrc);
        Assert.True(frame.CrcValid);
        Assert.Same(source, frame.Source);
        Assert.Equal(original, source.Data);
    }

    // docs/common-event-buffer.md, "CRC and invalid data": CRC failure preserves inspectable evidence.
    [Theory]
    [InlineData(CommonEventBufferVersion.V82)]
    [InlineData(CommonEventBufferVersion.V83)]
    [InlineData(CommonEventBufferVersion.V84)]
    [InlineData(CommonEventBufferVersion.V85)]
    public void Common_crc_failure_preserves_captured_crc_and_trailing_evidence(CommonEventBufferVersion version)
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(version);
        var expectedCrc = packet[79];
        packet[79] ^= 0xFF;
        byte[] bytes = [.. packet, 0x12];
        var original = bytes.ToArray();
        var source = CommonEventBufferDecoderTests.Source(bytes);

        var result = new CommonEventBufferDecoder().Decode(source, version);
        var frame = Assert.IsType<CommonEventBufferFrame>(result.Frame);

        Assert.Equal("COMMON_CRC_MISMATCH", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(expectedCrc, frame.ComputedCrc);
        Assert.Equal(original[79], frame.CapturedCrc);
        Assert.False(frame.CrcValid);
        Assert.False(frame.HostStateEligible);
        Assert.Equal(new byte[] { 0x12 }, frame.UnconsumedData);
        Assert.Equal(original, frame.Source.Data);
    }

    // docs/desay97-event-buffer.md, "Two-transaction transport" and "Payload":
    // count is the low nibble; the full reread supplies time and retains both physical locations.
    [Fact]
    public void Desay_full_reread_with_reserved_header_and_two_contacts_uses_phase_two_source()
    {
        var full = Seal([0x82, 0x20, 0x00, 0x00, 0xFF, 0xFF, 0x5F, 0xFF, 0xFF, 0x01, 0x00]);
        var original = full.ToArray();
        var probe = Record(10, [0x82]);
        var payload = Record(11, full);

        var assembly = new Desay97Assembler().Assemble([probe, payload]);
        var packet = Assert.Single(assembly.Packets);
        var decoded = new Desay97Decoder().Decode(packet, Desay97Profile.Standard);
        var frame = Assert.IsType<Desay97Frame>(decoded.Frame);

        Assert.Empty(assembly.Diagnostics);
        Assert.Empty(decoded.Diagnostics);
        Assert.Equal(2, frame.NumTouches);
        Assert.True(frame.HeaderReserved);
        Assert.Equal(0x82, frame.RawHeader);
        Assert.Collection(packet.PhysicalRecords,
            record => Assert.Same(probe, record), record => Assert.Same(payload, record));
        Assert.NotEqual(probe.Timestamp, payload.Timestamp);
        Assert.Equal(payload.Timestamp, frame.Source.Timestamp);
        Assert.Equal(payload.Location, frame.Source.Location);
        Assert.Equal(packet.StableId, frame.Source.StableId);
        Assert.Equal(original, packet.Data);
        Assert.Equal(original, payload.Data);
        Assert.Equal(new byte[] { 0x82 }, probe.Data);
        Assert.Collection(frame.Fingers,
            finger =>
            {
                Assert.Equal(0, finger.Id);
                Assert.Equal(TouchStatus.Enter, finger.Status);
                Assert.Equal((ushort)0, finger.X);
                Assert.Equal(ushort.MaxValue, finger.Y);
            },
            finger =>
            {
                Assert.Equal(15, finger.Id);
                Assert.Equal(TouchStatus.Move, finger.Status);
                Assert.Equal(ushort.MaxValue, finger.X);
                Assert.Equal((ushort)0x0100, finger.Y);
                Assert.True(finger.Reserved);
            });
    }

    // docs/desay97-event-buffer.md, "Two-transaction transport": n=0 still needs header plus CRC.
    [Theory]
    [InlineData(Desay97Profile.Standard)]
    [InlineData(Desay97Profile.BenzPalm)]
    public void Desay_zero_touch_reserved_header_is_all_break_for_either_explicit_profile(Desay97Profile profile)
    {
        var full = Seal([0x80]);
        var assembly = new Desay97Assembler().Assemble([Record(1, [0x80]), Record(2, full)]);
        var packet = Assert.Single(assembly.Packets);

        var result = new Desay97Decoder().Decode(packet, profile);
        var frame = Assert.IsType<Desay97Frame>(result.Frame);

        Assert.Empty(assembly.Diagnostics);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, packet.Data.Count);
        Assert.Equal(profile, frame.Profile);
        Assert.Equal(0, frame.NumTouches);
        Assert.True(frame.HeaderReserved);
        Assert.True(frame.AllBreak);
        Assert.Empty(frame.Fingers);
        Assert.True(frame.CrcValid);
        Assert.True(frame.HostStateEligible);
        Assert.Equal(full, frame.Packet.Data);
    }

    // docs/desay97-event-buffer.md, "Two-transaction transport": a zero-count probe alone is missing phase two.
    [Fact]
    public void Desay_zero_touch_probe_alone_is_discarded_with_its_source_location()
    {
        var probe = Record(1, [0x00]);

        var result = new Desay97Assembler().Assemble([probe]);

        Assert.Empty(result.Packets);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "DESAY97_MISSING_SECOND_PHASE");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(probe.StableId, diagnostic.SourceRecordId);
        Assert.Equal(probe.Location, diagnostic.Location);
        Assert.Equal(new byte[] { 0x00 }, probe.Data);
    }

    // docs/desay97-event-buffer.md, "Two-transaction transport": phase two must be exactly 1 + 5*n + 1 bytes.
    [Fact]
    public void Desay_zero_touch_extra_byte_is_discarded_and_does_not_swallow_next_pair()
    {
        var full = Seal([0x00]);
        var result = new Desay97Assembler().Assemble(
            [Record(1, [0x00]), Record(2, [.. full, 0xFF]), Record(3, [0x00]), Record(4, full)]);

        var packet = Assert.Single(result.Packets);
        Assert.Equal(3, packet.Probe.Index);
        Assert.Equal(4, packet.PayloadRead.Index);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DESAY97_LENGTH_MISMATCH", diagnostic.Code);
        Assert.Equal("2", diagnostic.Details?["expected"]);
        Assert.Equal("3", diagnostic.Details?["actual"]);
        Assert.Equal(full, packet.Data);
    }

    // docs/desay97-event-buffer.md, "Two-transaction transport" and "Payload":
    // even All Break requires CRC over the repeated header; a rejected pair cannot become a frame.
    [Fact]
    public void Desay_zero_touch_bad_crc_is_discarded_and_next_pair_remains_valid()
    {
        var full = Seal([0x80]);
        var corrupt = full.ToArray();
        corrupt[^1] ^= 0xFF;
        var original = corrupt.ToArray();
        var rejectedPayload = Record(2, corrupt);

        var result = new Desay97Assembler().Assemble(
            [Record(1, [0x80]), rejectedPayload, Record(3, [0x80]), Record(4, full)]);

        var packet = Assert.Single(result.Packets);
        Assert.Equal(3, packet.Probe.Index);
        Assert.Equal(4, packet.PayloadRead.Index);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("DESAY97_CRC_MISMATCH", diagnostic.Code);
        Assert.Equal(rejectedPayload.StableId, diagnostic.SourceRecordId);
        Assert.Equal(rejectedPayload.Location, diagnostic.Location);
        Assert.True(packet.CrcValid);
        Assert.Equal(original, rejectedPayload.Data);
    }

    // docs/desay97-event-buffer.md, "Two-transaction transport": the entire repeated header must match.
    [Theory]
    [InlineData(0x11)]
    [InlineData(0x21)]
    [InlineData(0x41)]
    [InlineData(0x81)]
    public void Desay_matching_touch_nibble_does_not_hide_changed_header_bits(byte repeatedHeader)
    {
        var full = Seal([repeatedHeader, 0x21, 0x00, 0x00, 0xFF, 0xFF]);
        var payload = Record(2, full);

        var result = new Desay97Assembler().Assemble([Record(1, [0x01]), payload]);

        Assert.Empty(result.Packets);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "DESAY97_HEADER_MISMATCH");
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(payload.StableId, diagnostic.SourceRecordId);
        Assert.Equal(payload.Location, diagnostic.Location);
        Assert.DoesNotContain(result.Diagnostics, item => item.Code is "DESAY97_LENGTH_MISMATCH" or "DESAY97_CRC_MISMATCH");
        Assert.Equal(full, payload.Data);
    }

    // docs/desay97-event-buffer.md, "Payload": bit 7 is profile-dependent, bit 4 is Reserved,
    // bits 6:5 are Event_ID (0 = Invalid, represented by the shared NoFinger enum value).
    // Benz expectations follow the documented contract; synthetic tests do not confirm its golden evidence gate.
    [Theory]
    [InlineData(0x10, TouchStatus.NoFinger, 0, false, true)]
    [InlineData(0x80, TouchStatus.NoFinger, 0, true, false)]
    [InlineData(0x2F, TouchStatus.Enter, 15, false, false)]
    [InlineData(0xBF, TouchStatus.Enter, 15, true, true)]
    [InlineData(0x50, TouchStatus.Move, 0, false, true)]
    [InlineData(0xC0, TouchStatus.Move, 0, true, false)]
    [InlineData(0x6F, TouchStatus.Break, 15, false, false)]
    [InlineData(0xFF, TouchStatus.Break, 15, true, true)]
    public void Desay_explicit_profiles_preserve_event_id_coordinates_reserved_bit_and_bytes(
        byte meta, TouchStatus status, byte id, bool profileFlag, bool reserved)
    {
        var full = Seal([0x01, meta, 0x00, 0x00, 0xFF, 0xFF]);
        var original = full.ToArray();
        var assembly = new Desay97Assembler().Assemble([Record(1, [0x01]), Record(2, full)]);
        var packet = Assert.Single(assembly.Packets);
        var decoder = new Desay97Decoder();

        var standardResult = decoder.Decode(packet, Desay97Profile.Standard);
        var benzResult = decoder.Decode(packet, Desay97Profile.BenzPalm);
        var standard = Assert.IsType<Desay97Frame>(standardResult.Frame);
        var benz = Assert.IsType<Desay97Frame>(benzResult.Frame);

        Assert.Empty(assembly.Diagnostics);
        Assert.Empty(standardResult.Diagnostics);
        Assert.Empty(benzResult.Diagnostics);
        Assert.Equal(Desay97Profile.Standard, standard.Profile);
        Assert.Equal(Desay97Profile.BenzPalm, benz.Profile);
        var standardFinger = Assert.Single(standard.Fingers);
        var benzFinger = Assert.Single(benz.Fingers);
        Assert.Equal(meta, standardFinger.RawMeta);
        Assert.Equal(id, standardFinger.Id);
        Assert.Equal(status, standardFinger.Status);
        Assert.Equal((ushort)0, standardFinger.X);
        Assert.Equal(ushort.MaxValue, standardFinger.Y);
        Assert.Equal(reserved, standardFinger.Reserved);
        Assert.Equal(profileFlag, standardFinger.Palm);
        Assert.False(standardFinger.Invalid);
        Assert.False(benzFinger.Palm);
        Assert.Equal(profileFlag, benzFinger.Invalid);
        Assert.Equal(standardFinger with { Palm = false }, benzFinger with { Invalid = false });
        Assert.Same(packet, standard.Packet);
        Assert.Same(packet, benz.Packet);
        Assert.Equal(original, packet.Data);
        Assert.Equal(original, packet.PayloadRead.Data);
    }

    private static SourceRecord Record(long index, byte[] data) =>
        CommonEventBufferDecoderTests.Source(data, index) with
        {
            Address = 0x99000,
            Timestamp = new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.FromHours(8))
                + TimeSpan.FromMilliseconds(index),
        };

    private static byte[] Seal(byte[] body) => [.. body, Crc8Poly1D.Compute(body)];
}
