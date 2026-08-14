using Nvt.Replay.Core;

namespace Nvt.Replay.Formats.Common;

public sealed class CommonEventBufferDecoder
{
    public const int FrameLength = 80;
    public const int FingerCount = 10;
    public const int FingerLength = 7;

    public CommonDecodeResult Decode(SourceRecord source, CommonEventBufferVersion version)
    {
        ArgumentNullException.ThrowIfNull(source);
        var diagnostics = new List<ReplayDiagnostic>();
        if (source.Data.Count < FrameLength)
        {
            diagnostics.Add(Diagnostic(
                source,
                DiagnosticSeverity.Error,
                "FORMAT_LENGTH_MISMATCH",
                $"Common event buffer requires {FrameLength} bytes; captured {source.Data.Count}.",
                new Dictionary<string, string>
                {
                    ["expected"] = FrameLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["actual"] = source.Data.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                }));
            return new CommonDecodeResult(null, diagnostics);
        }

        var packet = source.Data.Take(FrameLength).ToArray();
        var header = packet[0];
        var fingers = new CommonFinger[FingerCount];
        for (var index = 0; index < FingerCount; index++)
        {
            var offset = 1 + (index * FingerLength);
            var meta = packet[offset];
            fingers[index] = new CommonFinger(
                meta,
                (byte)((meta >> 4) & 0x0F),
                (TouchType)((meta >> 2) & 0x03),
                (TouchStatus)(meta & 0x03),
                ReadUInt16BigEndian(packet, offset + 1),
                ReadUInt16BigEndian(packet, offset + 3),
                packet.AsSpan(offset + 5, 2).ToArray());
        }

        var numTouches = (byte)(header & 0x0F);
        if (numTouches > FingerCount)
        {
            diagnostics.Add(Diagnostic(
                source,
                DiagnosticSeverity.Warning,
                "COMMON_TOUCH_COUNT_OUT_OF_RANGE",
                $"NumTouches is {numTouches}, above the public maximum {FingerCount}.",
                new Dictionary<string, string> { ["num_touches"] = numTouches.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        }

        var (buttonLength, asilOffset, reservedOffset, reservedLength) = version switch
        {
            CommonEventBufferVersion.V82 => (2, 73, 76, 3),
            CommonEventBufferVersion.V83 => (2, 73, 74, 5),
            CommonEventBufferVersion.V84 => (3, 74, 75, 3),
            CommonEventBufferVersion.V85 => (3, 74, 76, 2),
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        };

        var buttonRaw = packet.AsSpan(71, buttonLength).ToArray();
        var asilRaw = packet[asilOffset];
        var capturedCrc = packet[79];
        var computedCrc = CommonCrc8.Compute(packet.AsSpan(0, 79));
        var crcValid = capturedCrc == computedCrc;
        if (!crcValid)
        {
            diagnostics.Add(Diagnostic(
                source,
                DiagnosticSeverity.Warning,
                "COMMON_CRC_MISMATCH",
                $"Common event-buffer CRC mismatch: expected 0x{computedCrc:X2}, captured 0x{capturedCrc:X2}.",
                new Dictionary<string, string>
                {
                    ["expected"] = $"0x{computedCrc:X2}",
                    ["actual"] = $"0x{capturedCrc:X2}",
                }));
        }

        var allBreak = numTouches == 0 && packet.AsSpan(1, FingerCount * FingerLength).IndexOfAnyExcept((byte)0xFF) < 0;
        var frame = new CommonEventBufferFrame(
            version,
            source,
            header,
            (byte)((header >> 5) & 0x07),
            (header & 0x10) != 0,
            numTouches,
            fingers,
            new ButtonStatus(buttonRaw, buttonRaw[0] == 0xF8, "project_defined"),
            DecodeAsil(asilRaw, version == CommonEventBufferVersion.V82),
            allBreak,
            version == CommonEventBufferVersion.V82
                ? new CommonKnob(packet[74], packet[75], "todo")
                : null,
            version is CommonEventBufferVersion.V84 or CommonEventBufferVersion.V85
                ? DecodeBus(packet[78])
                : null,
            version == CommonEventBufferVersion.V85
                ? DecodeDiagnosticPacket(packet[75])
                : null,
            packet.AsSpan(reservedOffset, reservedLength).ToArray(),
            capturedCrc,
            computedCrc,
            crcValid,
            source.Data.Skip(FrameLength).ToArray(),
            crcValid && numTouches <= FingerCount);

        return new CommonDecodeResult(frame, diagnostics);
    }

    public static bool TryParseVersion(string value, out CommonEventBufferVersion version)
    {
        version = value.Trim().ToUpperInvariant() switch
        {
            "0X82" or "82" or "COMMON-82" => CommonEventBufferVersion.V82,
            "0X83" or "83" or "COMMON-83" => CommonEventBufferVersion.V83,
            "0X84" or "84" or "COMMON-84" => CommonEventBufferVersion.V84,
            "0X85" or "85" or "COMMON-85" => CommonEventBufferVersion.V85,
            _ => default,
        };
        return value.Trim().ToUpperInvariant() is
            "0X82" or "82" or "COMMON-82" or
            "0X83" or "83" or "COMMON-83" or
            "0X84" or "84" or "COMMON-84" or
            "0X85" or "85" or "COMMON-85";
    }

    private static AsilByte DecodeAsil(byte raw, bool resumeFlagDeprecated)
    {
        return new AsilByte(
            raw,
            (raw & 0x10) != 0,
            (raw & 0x08) != 0,
            (AsilErrorCode)(raw & 0x07),
            (raw & 0x1F) != 0,
            (byte)((raw >> 5) & 0x07),
            resumeFlagDeprecated);
    }

    private static CommonBusStatus DecodeBus(byte raw) =>
        new(raw, (byte)((raw >> 4) & 0x0F), (byte)(raw & 0x0F), 0x0F, "clamp_expected");

    private static CommonDiagnosticPacket DecodeDiagnosticPacket(byte raw)
    {
        var ems = (byte)((raw >> 1) & 0x0F);
        return new CommonDiagnosticPacket(
            raw,
            (byte)((raw >> 5) & 0x07),
            ems,
            Enumerable.Range(0, 4).Select(bit => (byte)((ems >> bit) & 1)).ToArray(),
            (raw & 0x01) != 0);
    }

    private static ushort ReadUInt16BigEndian(byte[] packet, int offset) =>
        (ushort)((packet[offset] << 8) | packet[offset + 1]);

    internal static ReplayDiagnostic Diagnostic(
        SourceRecord source,
        DiagnosticSeverity severity,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        new(severity, code, message, source.StableId, source.Location, details);
}
