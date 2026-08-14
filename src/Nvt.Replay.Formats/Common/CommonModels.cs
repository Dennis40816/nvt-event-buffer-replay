using Nvt.Replay.Core;

namespace Nvt.Replay.Formats.Common;

public enum CommonEventBufferVersion
{
    V82,
    V83,
    V84,
    V85,
}

public enum AsilErrorCode : byte
{
    None = 0,
    InitialFail = 1,
    RuntimeFail = 2,
    Reserved3 = 3,
    Reserved4 = 4,
    Reserved5 = 5,
    Reserved6 = 6,
    Reserved7 = 7,
}

public sealed record CommonCrcProfile(
    byte Polynomial,
    byte Initial,
    bool ReflectInput,
    bool ReflectOutput,
    byte XorOutput,
    EvidenceStatus EvidenceStatus);

public sealed record CommonFinger(
    byte RawMeta,
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    IReadOnlyList<byte> Reserved)
{
    public bool IsUnused =>
        RawMeta == 0xFF && X == 0xFFFF && Y == 0xFFFF && Reserved.All(value => value == 0xFF);

    public bool IsReported => !IsUnused && (Status != TouchStatus.NoFinger || Type == TouchType.Palm);
}

public sealed record ButtonStatus(
    IReadOnlyList<byte> Raw,
    bool Valid,
    string Mapping);

public sealed record AsilByte(
    byte Raw,
    bool Short,
    bool Open,
    AsilErrorCode ErrorCode,
    bool Alarm,
    byte UpperBits,
    bool ResumeFlagDeprecated);

public sealed record CommonBusStatus(
    byte Raw,
    byte NoResponseCounter,
    byte BusyCounter,
    byte CounterMaximum,
    string CounterBehavior);

public sealed record CommonDiagnosticPacket(
    byte Raw,
    byte Reserved,
    byte EmsBitmap,
    IReadOnlyList<byte> EmsBitsLsbFirst,
    bool PalmOn);

public sealed record CommonKnob(
    byte StatusRaw,
    byte AngleRaw,
    string Interpretation);

public sealed record CommonEventBufferFrame(
    CommonEventBufferVersion Version,
    SourceRecord Source,
    byte RawHeader,
    byte ReservedHeader,
    bool TpAsilError,
    byte NumTouches,
    IReadOnlyList<CommonFinger> Fingers,
    ButtonStatus Button,
    AsilByte Asil,
    bool AllBreak,
    CommonKnob? Knob,
    CommonBusStatus? BusStatus,
    CommonDiagnosticPacket? DiagnosticPacket,
    IReadOnlyList<byte> ReservedTail,
    byte CapturedCrc,
    byte ComputedCrc,
    bool CrcValid,
    IReadOnlyList<byte> UnconsumedData,
    bool HostStateEligible);

public sealed record CommonDecodeResult(
    CommonEventBufferFrame? Frame,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);
