using Nvt.Replay.Core;

namespace Nvt.Replay.Formats.Common;

/// <summary>Identifies the supported Common Event Buffer format version.</summary>
public enum CommonEventBufferVersion
{
    /// <summary>Identifies the V82 Common Event Buffer format.</summary>
    V82,
    /// <summary>Identifies the V83 Common Event Buffer format.</summary>
    V83,
    /// <summary>Identifies the V84 Common Event Buffer format.</summary>
    V84,
    /// <summary>Identifies the V85 Common Event Buffer format.</summary>
    V85,
}

/// <summary>Identifies the error code decoded from the low three bits of an ASIL byte.</summary>
public enum AsilErrorCode : byte
{
    /// <summary>Indicates no ASIL error.</summary>
    None = 0,
    /// <summary>Indicates an ASIL initialization failure.</summary>
    InitialFail = 1,
    /// <summary>Indicates an ASIL runtime failure.</summary>
    RuntimeFail = 2,
    /// <summary>Represents reserved ASIL error code 3.</summary>
    Reserved3 = 3,
    /// <summary>Represents reserved ASIL error code 4.</summary>
    Reserved4 = 4,
    /// <summary>Represents reserved ASIL error code 5.</summary>
    Reserved5 = 5,
    /// <summary>Represents reserved ASIL error code 6.</summary>
    Reserved6 = 6,
    /// <summary>Represents reserved ASIL error code 7.</summary>
    Reserved7 = 7,
}

/// <summary>Stores CRC algorithm parameters and their evidence status for a Common Event Buffer version.</summary>
/// <param name="Polynomial">The CRC polynomial byte.</param>
/// <param name="Initial">The initial CRC accumulator value.</param>
/// <param name="ReflectInput">Whether CRC input bytes are reflected.</param>
/// <param name="ReflectOutput">Whether the CRC result is reflected.</param>
/// <param name="XorOutput">The byte XORed with the CRC result.</param>
/// <param name="EvidenceStatus">The evidence status of the CRC profile.</param>
public sealed record CommonCrcProfile(
    byte Polynomial,
    byte Initial,
    bool ReflectInput,
    bool ReflectOutput,
    byte XorOutput,
    EvidenceStatus EvidenceStatus);

/// <summary>Stores a decoded Common Event Buffer contact and its captured metadata and reserved bytes.</summary>
/// <param name="RawMeta">The captured contact metadata byte.</param>
/// <param name="Id">The decoded contact identifier.</param>
/// <param name="Type">The decoded touch type.</param>
/// <param name="Status">The decoded touch status.</param>
/// <param name="X">The decoded X coordinate.</param>
/// <param name="Y">The decoded Y coordinate.</param>
/// <param name="Reserved">The captured reserved bytes in the contact slot.</param>
public sealed record CommonFinger(
    byte RawMeta,
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    IReadOnlyList<byte> Reserved)
{
    /// <summary>Indicates whether metadata and reserved bytes are 0xFF and both coordinates are 0xFFFF.</summary>
    public bool IsUnused =>
        RawMeta == 0xFF && X == 0xFFFF && Y == 0xFFFF && Reserved.All(value => value == 0xFF);

    /// <summary>Indicates whether a used slot has a status other than NoFinger or has the Palm type.</summary>
    public bool IsReported => !IsUnused && (Status != TouchStatus.NoFinger || Type == TouchType.Palm);
}

/// <summary>Stores captured button bytes, their validity flag, and their mapping description.</summary>
/// <param name="Raw">The captured button status bytes.</param>
/// <param name="Valid">Whether the first button byte is the expected 0xF8 marker.</param>
/// <param name="Mapping">The recorded button mapping label.</param>
public sealed record ButtonStatus(
    IReadOnlyList<byte> Raw,
    bool Valid,
    string Mapping);

/// <summary>Stores the captured ASIL byte and its decoded flags and error code.</summary>
/// <param name="Raw">The captured ASIL byte.</param>
/// <param name="Short">Whether bit 4 of the ASIL byte is set.</param>
/// <param name="Open">Whether bit 3 of the ASIL byte is set.</param>
/// <param name="ErrorCode">The error code decoded from the low three bits.</param>
/// <param name="Alarm">Whether any of the ASIL byte's five low bits are set.</param>
/// <param name="UpperBits">The upper three bits of the ASIL byte, shifted down.</param>
/// <param name="ResumeFlagDeprecated">Whether the selected format deprecates the resume flag.</param>
public sealed record AsilByte(
    byte Raw,
    bool Short,
    bool Open,
    AsilErrorCode ErrorCode,
    bool Alarm,
    byte UpperBits,
    bool ResumeFlagDeprecated);

/// <summary>Stores the captured bus status byte and its decoded counters and counter metadata.</summary>
/// <param name="Raw">The captured bus status byte.</param>
/// <param name="NoResponseCounter">The no-response counter decoded from the high nibble.</param>
/// <param name="BusyCounter">The busy counter decoded from the low nibble.</param>
/// <param name="CounterMaximum">The maximum representable value of either bus counter.</param>
/// <param name="CounterBehavior">The description of the expected bus counter behavior.</param>
public sealed record CommonBusStatus(
    byte Raw,
    byte NoResponseCounter,
    byte BusyCounter,
    byte CounterMaximum,
    string CounterBehavior);

/// <summary>Stores the captured diagnostic byte, its EMS bitmap, and the decoded palm flag.</summary>
/// <param name="Raw">The captured diagnostic byte.</param>
/// <param name="Reserved">The upper three reserved bits, shifted down.</param>
/// <param name="EmsBitmap">The four-bit EMS bitmap decoded from bits 1 through 4.</param>
/// <param name="EmsBitsLsbFirst">The EMS bits ordered from least to most significant.</param>
/// <param name="PalmOn">Whether bit 0 of the diagnostic byte is set.</param>
public sealed record CommonDiagnosticPacket(
    byte Raw,
    byte Reserved,
    byte EmsBitmap,
    IReadOnlyList<byte> EmsBitsLsbFirst,
    bool PalmOn);

/// <summary>Stores captured knob status and angle bytes with an interpretation description.</summary>
/// <param name="StatusRaw">The captured knob status byte.</param>
/// <param name="AngleRaw">The captured knob angle byte.</param>
/// <param name="Interpretation">The recorded interpretation label for the knob bytes.</param>
public sealed record CommonKnob(
    byte StatusRaw,
    byte AngleRaw,
    string Interpretation);

/// <summary>Stores a decoded Common Event Buffer frame with its source, CRC results, and host-state eligibility.</summary>
/// <param name="Version">The Common Event Buffer version used to decode the frame.</param>
/// <param name="Source">The source record containing the captured frame bytes.</param>
/// <param name="RawHeader">The captured frame header byte.</param>
/// <param name="ReservedHeader">The upper three reserved header bits, shifted down.</param>
/// <param name="TpAsilError">Whether the frame header reports a TP ASIL error.</param>
/// <param name="NumTouches">The touch count decoded from the frame header.</param>
/// <param name="Fingers">The decoded contact slots in source order.</param>
/// <param name="Button">The decoded button status.</param>
/// <param name="Asil">The decoded ASIL byte.</param>
/// <param name="AllBreak">Whether the touch count is zero and every byte in the contact slots is 0xFF.</param>
/// <param name="Knob">The decoded knob data, when present.</param>
/// <param name="BusStatus">The decoded bus status, when present.</param>
/// <param name="DiagnosticPacket">The decoded diagnostic packet, when present.</param>
/// <param name="ReservedTail">The captured reserved bytes near the end of the frame.</param>
/// <param name="CapturedCrc">The CRC byte captured at the end of the frame.</param>
/// <param name="ComputedCrc">The CRC computed from the frame bytes preceding the captured CRC.</param>
/// <param name="CrcValid">Whether the captured CRC equals the computed CRC.</param>
/// <param name="UnconsumedData">The source bytes remaining after the decoded frame.</param>
/// <param name="HostStateEligible">Whether the frame passes the decoder's host-state eligibility checks.</param>
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

/// <summary>Stores the decoded frame, if available, and the diagnostics produced during decoding.</summary>
/// <param name="Frame">The decoded frame, or null when decoding cannot produce one.</param>
/// <param name="Diagnostics">The diagnostics produced while decoding the source record.</param>
public sealed record CommonDecodeResult(
    CommonEventBufferFrame? Frame,
    IReadOnlyList<ReplayDiagnostic> Diagnostics);
