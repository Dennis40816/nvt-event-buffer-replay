using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Tests;

public sealed class Desay97Tests
{
    [Fact]
    public void Crc_matches_verified_reference_vector()
    {
        Assert.Equal(0x17, Crc8Poly1D.Compute(Convert.FromHexString("016103140719")));
    }

    [Fact]
    public void Assembler_pairs_full_second_read_and_preserves_both_sources()
    {
        var full = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        var result = new Desay97Assembler().Assemble([Record(10, [0x01]), Record(11, full)]);

        var packet = Assert.Single(result.Packets);
        Assert.Equal(full, packet.Data);
        Assert.Equal([10L, 11L], packet.PhysicalRecords.Select(record => record.Index));
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Assembler_uses_only_the_selected_7bit_slave_address()
    {
        var full = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        var result = new Desay97Assembler(0x99000, 0x2A).Assemble(
        [
            Record(1, [0x01], slaveAddress: 0x01),
            Record(2, full, slaveAddress: 0x01),
            Record(3, [0x01], slaveAddress: 0x2A),
            Record(4, full, slaveAddress: 0x2A),
        ]);

        var packet = Assert.Single(result.Packets);
        Assert.All(packet.PhysicalRecords, record => Assert.Equal(0x2A, record.I2c?.SlaveAddress));
    }

    [Fact]
    public void Assembler_owns_crc_calculation_and_decoder_consumes_the_verified_evidence()
    {
        var full = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        var assembly = new Desay97Assembler().Assemble([Record(10, [0x01]), Record(11, full)]);
        var packet = Assert.Single(assembly.Packets);

        Assert.Equal(full[^1], packet.CapturedCrc);
        Assert.Equal(Crc8Poly1D.Compute(full.AsSpan(0, full.Length - 1)), packet.ComputedCrc);
        Assert.True(packet.CrcValid);

        var decoded = new Desay97Decoder().Decode(packet, Desay97Profile.Standard);
        var frame = Assert.IsType<Desay97Frame>(decoded.Frame);

        Assert.Equal(packet.CapturedCrc, frame.CapturedCrc);
        Assert.Equal(packet.ComputedCrc, frame.ComputedCrc);
        Assert.True(frame.CrcValid);
        Assert.DoesNotContain(decoded.Diagnostics, item => item.Code == "DESAY97_CRC_MISMATCH");
    }

    [Fact]
    public void Three_parameter_packet_constructor_remains_compatible_and_prevalidates_crc()
    {
        var full = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);

        var packet = new Desay97AssembledPacket(Record(1, [0x01]), Record(2, full), full);
        var decoded = new Desay97Decoder().Decode(packet, Desay97Profile.Standard);

        Assert.Equal(full[^1], packet.CapturedCrc);
        Assert.Equal(Crc8Poly1D.Compute(full.AsSpan(0, full.Length - 1)), packet.ComputedCrc);
        Assert.True(packet.CrcValid);
        Assert.True(Assert.IsType<Desay97Frame>(decoded.Frame).CrcValid);
        Assert.DoesNotContain(decoded.Diagnostics, item => item.Code == "DESAY97_CRC_MISMATCH");
    }

    [Fact]
    public void Invalid_crc_is_reported_once_and_dropped_at_the_assembly_boundary()
    {
        var corrupt = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        corrupt[^1] ^= 0xFF;

        var assembly = new Desay97Assembler().Assemble([Record(1, [0x01]), Record(2, corrupt)]);

        Assert.Empty(assembly.Packets);
        Assert.Single(assembly.Diagnostics, item => item.Code == "DESAY97_CRC_MISMATCH");
    }

    [Fact]
    public void Zero_touch_still_requires_a_full_header_and_crc_second_read()
    {
        var result = new Desay97Assembler().Assemble([Record(1, [0x00]), Record(2, [0x00, 0x00])]);
        var decoded = new Desay97Decoder().Decode(Assert.Single(result.Packets), Desay97Profile.Standard);

        Assert.True(Assert.IsType<Desay97Frame>(decoded.Frame).AllBreak);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Standard_and_benz_profiles_change_only_flag_semantics()
    {
        var full = Seal([0x01, 0xE1, 0x03, 0x14, 0x07, 0x19]);
        var packet = Assert.Single(new Desay97Assembler().Assemble([Record(1, [0x01]), Record(2, full)]).Packets);

        var standard = Assert.IsType<Desay97Frame>(new Desay97Decoder().Decode(packet, Desay97Profile.Standard).Frame);
        var benz = Assert.IsType<Desay97Frame>(new Desay97Decoder().Decode(packet, Desay97Profile.BenzPalm).Frame);

        Assert.True(Assert.Single(standard.Fingers).Palm);
        Assert.False(Assert.Single(standard.Fingers).Invalid);
        Assert.False(Assert.Single(benz.Fingers).Palm);
        Assert.True(Assert.Single(benz.Fingers).Invalid);
        Assert.Equal(full, standard.Packet.Data);
        Assert.Equal(full, benz.Packet.Data);
        Assert.Equal(TouchStatus.Break, Assert.Single(standard.Fingers).Status);
    }

    [Fact]
    public void Missing_phase_at_capture_end_warns_and_drops()
    {
        var result = new Desay97Assembler().Assemble([Record(1, [0x01])]);

        Assert.Empty(result.Packets);
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_MISSING_SECOND_PHASE");
    }

    [Fact]
    public void Assembler_observes_cancellation_between_physical_records()
    {
        using var cancellation = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new Desay97Assembler().Assemble(Records(), cancellation.Token));

        IEnumerable<SourceRecord> Records()
        {
            yield return Record(1, [0x01]);
            cancellation.Cancel();
            yield return Record(2, Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]));
        }
    }

    [Fact]
    public void Orphan_and_duplicate_second_phases_warn_and_drop()
    {
        var full = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        var result = new Desay97Assembler().Assemble([Record(1, full), Record(2, [0x01]), Record(3, full), Record(4, full)]);

        Assert.Single(result.Packets);
        Assert.Equal(2, result.Diagnostics.Count(item => item.Code == "DESAY97_ORPHAN_SECOND_PHASE"));
    }

    [Fact]
    public void Intervening_transaction_invalidates_pending_pair()
    {
        var result = new Desay97Assembler().Assemble(
        [
            Record(1, [0x01]),
            Record(2, [0xA3], address: 0x99060),
            Record(3, Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19])),
        ]);

        Assert.Empty(result.Packets);
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_INTERVENING_TRANSACTION");
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_MISSING_SECOND_PHASE");
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_ORPHAN_SECOND_PHASE");
    }

    [Fact]
    public void Legacy_plus_one_continuation_is_not_accepted_as_the_confirmed_full_reread_protocol()
    {
        var full = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        var result = new Desay97Assembler().Assemble([Record(1, [0x01]), Record(2, full, address: 0x99001)]);

        Assert.Empty(result.Packets);
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_INTERVENING_TRANSACTION");
    }

    [Fact]
    public void Duplicate_probe_replaces_pending_probe_with_warning()
    {
        var full = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        var result = new Desay97Assembler().Assemble([Record(1, [0x01]), Record(2, [0x01]), Record(3, full)]);

        Assert.Single(result.Packets);
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_DUPLICATE_FIRST_PHASE");
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_MISSING_SECOND_PHASE");
    }

    [Fact]
    public void Invalid_length_header_or_crc_warns_and_drops_each_pair()
    {
        var good = Seal([0x01, 0x61, 0x03, 0x14, 0x07, 0x19]);
        var badCrc = good.ToArray();
        badCrc[^1] ^= 0xFF;
        var result = new Desay97Assembler().Assemble(
        [
            Record(1, [0x01]), Record(2, [0x01, 0x00]),
            Record(3, [0x01]), Record(4, [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]),
            Record(5, [0x01]), Record(6, badCrc),
        ]);

        Assert.Empty(result.Packets);
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_LENGTH_MISMATCH");
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_HEADER_MISMATCH");
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_CRC_MISMATCH");
    }

    [Fact]
    public void Desay_reuses_host_state_reducer_and_keeps_the_physical_pair()
    {
        var full = Seal([0x01, 0x41, 0x03, 0x14, 0x07, 0x19]);
        var packet = Assert.Single(new Desay97Assembler().Assemble([Record(10, [0x01]), Record(11, full)]).Packets);
        var frame = Assert.IsType<Desay97Frame>(new Desay97Decoder().Decode(packet, Desay97Profile.Standard).Frame);
        var replay = new Desay97ReplaySession([frame]);

        var snapshot = replay.Seek(0);

        Assert.Equal(2, snapshot.PhysicalRecords.Count);
        Assert.Equal(TouchStatus.Move, Assert.Single(snapshot.HostContacts).Status);
        Assert.Equal(frame.Packet.StableId, Assert.Single(snapshot.ReportedContacts).SourceRecordId);
    }

    [Fact]
    public void Benz_invalid_contact_stays_visible_as_evidence_but_not_host_state()
    {
        var full = Seal([0x01, 0xC1, 0x03, 0x14, 0x07, 0x19]);
        var packet = Assert.Single(new Desay97Assembler().Assemble([Record(1, [0x01]), Record(2, full)]).Packets);
        var frame = Assert.IsType<Desay97Frame>(new Desay97Decoder().Decode(packet, Desay97Profile.BenzPalm).Frame);
        var snapshot = new Desay97ReplaySession([frame]).Seek(0);

        Assert.True(Assert.Single(snapshot.ReportedContacts).Invalid);
        Assert.Empty(snapshot.HostContacts);
    }

    [Fact]
    public void Short_open_and_tp_asil_header_bits_are_alarms()
    {
        var full = Seal([0x71, 0x41, 0x03, 0x14, 0x07, 0x19]);
        var packet = Assert.Single(new Desay97Assembler().Assemble([Record(1, [0x71]), Record(2, full)]).Packets);

        var result = new Desay97Decoder().Decode(packet, Desay97Profile.Standard);

        Assert.Equal(3, result.Diagnostics.Count);
        Assert.All(result.Diagnostics, item => Assert.Equal(DiagnosticSeverity.Alarm, item.Severity));
    }

    [Fact]
    public void More_than_ten_contacts_remain_visible_but_cannot_update_host_state()
    {
        var body = new List<byte> { 0x0B };
        for (byte id = 1; id <= 11; id++)
        {
            body.Add((byte)(0x40 | id));
            body.AddRange([0x00, id, 0x00, id]);
        }
        var full = Seal(body);
        var packet = Assert.Single(new Desay97Assembler().Assemble([Record(1, [0x0B]), Record(2, full)]).Packets);

        var result = new Desay97Decoder().Decode(packet, Desay97Profile.Standard);

        Assert.Equal(11, result.Frame?.Fingers.Count);
        Assert.False(result.Frame?.HostStateEligible);
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_TOUCH_COUNT_OUT_OF_RANGE");
    }

    [Fact]
    public void Duplicate_contact_ids_remain_visible_but_cannot_update_host_state()
    {
        var full = Seal([
            0x02,
            0x41, 0x00, 0x64, 0x00, 0xC8,
            0x41, 0x01, 0x2C, 0x01, 0x90,
        ]);
        var packet = Assert.Single(new Desay97Assembler().Assemble([Record(1, [0x02]), Record(2, full)]).Packets);

        var result = new Desay97Decoder().Decode(packet, Desay97Profile.Standard);
        var frame = Assert.IsType<Desay97Frame>(result.Frame);

        Assert.Equal(2, frame.Fingers.Count);
        Assert.False(frame.HostStateEligible);
        Assert.Empty(new Desay97ReplaySession([frame]).Seek(0).HostContacts);
        Assert.Contains(result.Diagnostics, item => item.Code == "DESAY97_DUPLICATE_CONTACT_ID");
    }

    [Fact]
    public void Dropped_crc_pair_cannot_mutate_prior_host_state()
    {
        var enter = Seal([0x01, 0x21, 0x03, 0x14, 0x07, 0x19]);
        var corruptMove = Seal([0x01, 0x41, 0x03, 0x20, 0x07, 0x30]);
        corruptMove[^1] ^= 0xFF;
        var assembly = new Desay97Assembler().Assemble(
        [
            Record(1, [0x01]), Record(2, enter),
            Record(3, [0x01]), Record(4, corruptMove),
        ]);
        var decoder = new Desay97Decoder();
        var frames = assembly.Packets
            .Select(packet => Assert.IsType<Desay97Frame>(decoder.Decode(packet, Desay97Profile.Standard).Frame))
            .ToArray();

        var contact = Assert.Single(new Desay97ReplaySession(frames).Seek(0).HostContacts);

        Assert.Single(frames);
        Assert.Equal((ushort)0x0314, contact.X);
        Assert.Contains(assembly.Diagnostics, item => item.Code == "DESAY97_CRC_MISMATCH");
    }

    private static SourceRecord Record(
        long index,
        IReadOnlyList<byte> data,
        uint address = 0x99000,
        int? slaveAddress = null) =>
        new(
            index,
            $"capture:L{index + 1}",
            DateTimeOffset.Parse("2026-08-14T10:00:00+08:00") + TimeSpan.FromMilliseconds(index * 10),
            BusOperation.Read,
            "TP",
            address,
            data.Count,
            data,
            string.Empty,
            new SourceLocation(index * 10, (int)index + 1),
            slaveAddress is { } slave ? new I2cTransport(slave, [], [], null) : null);

    private static byte[] Seal(IReadOnlyList<byte> bytes) =>
        [.. bytes, Crc8Poly1D.Compute(bytes.ToArray())];
}
