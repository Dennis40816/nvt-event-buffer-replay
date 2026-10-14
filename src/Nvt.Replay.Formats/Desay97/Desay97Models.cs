using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Formats.Desay97;

/// <summary>Selects how bit 7 of a Desay 0x97 contact's metadata is interpreted.</summary>
public enum Desay97Profile
{
    /// <summary>Interprets contact metadata bit 7 as the palm flag.</summary>
    Standard,
    /// <summary>Interprets contact metadata bit 7 as the invalid flag.</summary>
    BenzPalm,
}

/// <summary>Holds a decoded Desay 0x97 contact with its raw metadata, coordinates and profile flags.</summary>
/// <param name="RawMeta">The unmodified metadata byte for the contact.</param>
/// <param name="Id">The contact identifier decoded from the low four metadata bits.</param>
/// <param name="Status">The touch status decoded from metadata bits 5 and 6.</param>
/// <param name="X">The contact's X coordinate decoded from two big-endian bytes.</param>
/// <param name="Y">The contact's Y coordinate decoded from two big-endian bytes.</param>
/// <param name="Palm">Whether metadata bit 7 is set under the Standard profile.</param>
/// <param name="Invalid">Whether metadata bit 7 is set under the BenzPalm profile.</param>
/// <param name="Reserved">Whether reserved metadata bit 4 is set.</param>
public sealed record Desay97Finger(
    byte RawMeta,
    byte Id,
    TouchStatus Status,
    ushort X,
    ushort Y,
    bool Palm,
    bool Invalid,
    bool Reserved);

/// <summary>Holds a Desay 0x97 two-transaction packet with its source records, bytes and CRC values.</summary>
/// <param name="Probe">The first-phase source record containing the one-byte header probe.</param>
/// <param name="PayloadRead">The second-phase source record containing the full payload.</param>
/// <param name="Data">The assembled payload bytes, including the header and trailing CRC.</param>
/// <param name="CapturedCrc">The CRC byte captured at the end of the payload.</param>
/// <param name="ComputedCrc">The CRC computed over the payload bytes excluding the trailing CRC.</param>
public sealed record Desay97AssembledPacket(
    SourceRecord Probe,
    SourceRecord PayloadRead,
    IReadOnlyList<byte> Data,
    byte CapturedCrc,
    byte ComputedCrc)
{
    /// <summary>Creates a packet from source records and payload bytes, deriving the captured and computed CRC values.</summary>
    public Desay97AssembledPacket(
        SourceRecord probe,
        SourceRecord payloadRead,
        IReadOnlyList<byte> data)
        : this(
            probe,
            payloadRead,
            data,
            data.Count == 0 ? (byte)0 : data[^1],
            Crc8Poly1D.Compute(data.Take(Math.Max(0, data.Count - 1)).ToArray()))
    {
    }

    /// <summary>Gets the probe and payload source records in transaction order.</summary>
    public IReadOnlyList<SourceRecord> PhysicalRecords => [Probe, PayloadRead];

    /// <summary>Gets the packet identifier formed by joining the probe and payload source identifiers.</summary>
    public string StableId => $"{Probe.StableId}+{PayloadRead.StableId}";

    /// <summary>Gets whether the captured and computed packet CRC values match.</summary>
    public bool CrcValid => CapturedCrc == ComputedCrc;
}

/// <summary>Holds a decoded Desay 0x97 frame with header flags, contacts, CRC values and Host State eligibility.</summary>
/// <param name="Profile">The profile used to interpret contact metadata bit 7.</param>
/// <param name="Packet">The assembled packet from which the frame was decoded.</param>
/// <param name="RawHeader">The unmodified first byte of the payload.</param>
/// <param name="HeaderReserved">Whether reserved header bit 7 is set.</param>
/// <param name="Short">Whether header bit 6 asserts the Short alarm.</param>
/// <param name="Open">Whether header bit 5 asserts the Open alarm.</param>
/// <param name="TpAsilError">Whether header bit 4 asserts the TP ASIL Error alarm.</param>
/// <param name="NumTouches">The contact count decoded from the low four header bits.</param>
/// <param name="Fingers">The contacts decoded from the frame payload.</param>
/// <param name="AllBreak">Whether the frame reports zero contacts.</param>
/// <param name="CapturedCrc">The CRC byte captured at the end of the payload.</param>
/// <param name="ComputedCrc">The CRC computed over the payload bytes excluding the trailing CRC.</param>
/// <param name="CrcValid">Whether the captured and computed CRC values match.</param>
/// <param name="HostStateEligible">Whether CRC validation, the ten-contact limit and unique IDs permit a Host State update.</param>
public sealed record Desay97Frame(
    Desay97Profile Profile,
    Desay97AssembledPacket Packet,
    byte RawHeader,
    bool HeaderReserved,
    bool Short,
    bool Open,
    bool TpAsilError,
    byte NumTouches,
    IReadOnlyList<Desay97Finger> Fingers,
    bool AllBreak,
    byte CapturedCrc,
    byte ComputedCrc,
    bool CrcValid,
    bool HostStateEligible)
{
    /// <summary>Gets the payload source record with its stable identifier replaced by the assembled packet identifier.</summary>
    public SourceRecord Source => PayloadSource(Packet);

    private static SourceRecord PayloadSource(Desay97AssembledPacket packet) =>
        packet.PayloadRead with { StableId = packet.StableId };
}

/// <summary>Holds assembled Desay 0x97 packets and diagnostics from the assembly process.</summary>
/// <param name="Packets">The packets successfully assembled from source records.</param>
/// <param name="Diagnostics">The diagnostics emitted while assembling packets.</param>
public sealed record Desay97AssemblyResult(
    IReadOnlyList<Desay97AssembledPacket> Packets,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);

/// <summary>Holds a decoded Desay 0x97 frame, when available, and decoding diagnostics.</summary>
/// <param name="Frame">The decoded frame, or null when the payload cannot be decoded.</param>
/// <param name="Diagnostics">The diagnostics emitted while decoding the packet.</param>
public sealed record Desay97DecodeResult(
    Desay97Frame? Frame,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);
