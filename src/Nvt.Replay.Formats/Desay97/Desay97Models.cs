using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Formats.Desay97;

public enum Desay97Profile
{
    Standard,
    BenzPalm,
}

public sealed record Desay97Finger(
    byte RawMeta,
    byte Id,
    TouchStatus Status,
    ushort X,
    ushort Y,
    bool Palm,
    bool Invalid,
    bool Reserved);

public sealed record Desay97AssembledPacket(
    SourceRecord Probe,
    SourceRecord PayloadRead,
    IReadOnlyList<byte> Data,
    byte CapturedCrc,
    byte ComputedCrc)
{
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

    public IReadOnlyList<SourceRecord> PhysicalRecords => [Probe, PayloadRead];

    public string StableId => $"{Probe.StableId}+{PayloadRead.StableId}";

    public bool CrcValid => CapturedCrc == ComputedCrc;
}

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
    public SourceRecord Source => PayloadSource(Packet);

    private static SourceRecord PayloadSource(Desay97AssembledPacket packet) =>
        packet.PayloadRead with { StableId = packet.StableId };
}

public sealed record Desay97AssemblyResult(
    IReadOnlyList<Desay97AssembledPacket> Packets,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);

public sealed record Desay97DecodeResult(
    Desay97Frame? Frame,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);
