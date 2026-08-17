using System.Globalization;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Rendering;

public sealed record RawByteSection(
    string Offset,
    string Label,
    string Bytes,
    string Detail);

public sealed record RawFrameLayout(
    string Title,
    string Summary,
    IReadOnlyList<RawByteSection> Sections);

public static class RawFrameLayoutBuilder
{
    private const int GenericPreviewLimit = 256;

    public static RawFrameLayout Build(SourceRecord record, object? decodedFrame)
    {
        ArgumentNullException.ThrowIfNull(record);
        return decodedFrame switch
        {
            CommonEventBufferFrame common => Common(record.Data, common),
            Desay97Frame desay => Desay(desay),
            _ => Generic(record.Data),
        };
    }

    private static RawFrameLayout Common(IReadOnlyList<byte> data, CommonEventBufferFrame frame)
    {
        var sections = new List<RawByteSection>
        {
            Section(data, 0, 1, "HEADER", $"{frame.NumTouches} touches, TP ASIL {(frame.TpAsilError ? "alarm" : "clear")}"),
        };
        for (var index = 0; index < CommonEventBufferDecoder.FingerCount; index++)
        {
            var finger = frame.Fingers[index];
            var detail = finger.IsUnused
                ? "unused"
                : $"ID {finger.Id}, {finger.Type} {finger.Status}, X {finger.X}, Y {finger.Y}";
            sections.Add(Section(
                data,
                1 + (index * CommonEventBufferDecoder.FingerLength),
                CommonEventBufferDecoder.FingerLength,
                $"TOUCH {index + 1}",
                detail));
        }

        sections.Add(Section(data, 71, frame.Button.Raw.Count, "BUTTON", frame.Button.Valid ? "valid, project-defined" : "not valid"));
        sections.Add(Section(data, frame.Version is CommonEventBufferVersion.V84 or CommonEventBufferVersion.V85 ? 74 : 73, 1, "ASIL", AsilDetail(frame.Asil)));
        switch (frame.Version)
        {
            case CommonEventBufferVersion.V82:
                sections.Add(Section(data, 74, 2, "KNOB", "raw status and angle, interpretation TODO"));
                sections.Add(Section(data, 76, 3, "RESERVED", "preserved"));
                break;
            case CommonEventBufferVersion.V83:
                sections.Add(Section(data, 74, 5, "RESERVED", "preserved"));
                break;
            case CommonEventBufferVersion.V84:
                sections.Add(Section(data, 75, 3, "RESERVED", "preserved"));
                sections.Add(Section(data, 78, 1, "BUS", BusDetail(frame.BusStatus)));
                break;
            case CommonEventBufferVersion.V85:
                sections.Add(Section(data, 75, 1, "DIAGNOSTIC", DiagnosticDetail(frame.DiagnosticPacket)));
                sections.Add(Section(data, 76, 2, "RESERVED", "preserved"));
                sections.Add(Section(data, 78, 1, "BUS", BusDetail(frame.BusStatus)));
                break;
        }
        sections.Add(Section(data, 79, 1, "CRC", frame.CrcValid ? "OK" : $"FAIL, expected {frame.ComputedCrc:X2}"));
        if (data.Count > CommonEventBufferDecoder.FrameLength)
        {
            var extraLength = data.Count - CommonEventBufferDecoder.FrameLength;
            sections.Add(Section(data, CommonEventBufferDecoder.FrameLength, Math.Min(16, extraLength), "EXTRA", $"{extraLength} unconsumed bytes retained"));
        }

        return new RawFrameLayout(
            $"Common {Version(frame.Version)} · {data.Count} bytes",
            "1-byte header, 10 × 7-byte touch slots, version tail, CRC",
            sections);
    }

    private static RawFrameLayout Desay(Desay97Frame frame)
    {
        var data = frame.Packet.Data;
        var sections = new List<RawByteSection>
        {
            Section(data, 0, 1, "HEADER", $"{frame.NumTouches} touches, short {OnOff(frame.Short)}, open {OnOff(frame.Open)}, TP ASIL {OnOff(frame.TpAsilError)}"),
        };
        for (var index = 0; index < frame.Fingers.Count; index++)
        {
            var finger = frame.Fingers[index];
            var type = finger.Invalid ? "Invalid" : finger.Palm ? "Palm" : "Finger";
            sections.Add(Section(data, 1 + (index * 5), 5, $"TOUCH {index + 1}", $"ID {finger.Id}, {type} {finger.Status}, X {finger.X}, Y {finger.Y}"));
        }
        sections.Add(Section(data, data.Count - 1, 1, "CRC", frame.CrcValid ? "OK" : $"FAIL, expected {frame.ComputedCrc:X2}"));
        return new RawFrameLayout(
            $"Desay 0x97 / {Profile(frame.Profile)} · {data.Count} bytes",
            $"phase 2 of two-transaction protocol, 1-byte header, {frame.NumTouches} × 5-byte touch slots, CRC",
            sections);
    }

    private static RawFrameLayout Generic(IReadOnlyList<byte> data)
    {
        var shown = Math.Min(data.Count, GenericPreviewLimit);
        var sections = new List<RawByteSection>();
        for (var offset = 0; offset < shown; offset += 8)
        {
            var length = Math.Min(8, shown - offset);
            sections.Add(Section(data, offset, length, "DATA", "untyped source bytes"));
        }
        if (data.Count > shown)
            sections.Add(new RawByteSection(HexRange(shown, 0), "RETAINED", string.Empty, $"{data.Count - shown:N0} additional bytes available in flat view"));
        return new RawFrameLayout("Raw transaction", $"{data.Count:N0} bytes, no decoded frame layout", sections);
    }

    private static RawByteSection Section(IReadOnlyList<byte> data, int offset, int length, string label, string detail)
    {
        var available = Math.Max(0, Math.Min(length, data.Count - offset));
        var bytes = available == 0
            ? string.Empty
            : string.Join(' ', data.Skip(offset).Take(available).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        return new RawByteSection(HexRange(offset, available), label, bytes, detail);
    }

    private static string HexRange(int offset, int length)
    {
        var width = offset + Math.Max(0, length - 1) > byte.MaxValue ? 4 : 2;
        return length <= 1
            ? offset.ToString($"X{width}", CultureInfo.InvariantCulture)
            : $"{offset.ToString($"X{width}", CultureInfo.InvariantCulture)}-{(offset + length - 1).ToString($"X{width}", CultureInfo.InvariantCulture)}";
    }

    private static string AsilDetail(AsilByte asil) =>
        asil.Alarm
            ? $"alarm, short {OnOff(asil.Short)}, open {OnOff(asil.Open)}, code {(byte)asil.ErrorCode}"
            : "clear";

    private static string BusDetail(CommonBusStatus? bus) => bus is null
        ? "not present"
        : $"no-response {bus.NoResponseCounter}, busy {bus.BusyCounter}";

    private static string DiagnosticDetail(CommonDiagnosticPacket? diagnostic) => diagnostic is null
        ? "not present"
        : $"EMS {Convert.ToString(diagnostic.EmsBitmap, 2).PadLeft(4, '0')}, global palm {OnOff(diagnostic.PalmOn)}";

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string Version(CommonEventBufferVersion version) => version switch
    {
        CommonEventBufferVersion.V82 => "0x82",
        CommonEventBufferVersion.V83 => "0x83",
        CommonEventBufferVersion.V84 => "0x84",
        CommonEventBufferVersion.V85 => "0x85",
        _ => version.ToString(),
    };

    private static string Profile(Desay97Profile profile) => profile switch
    {
        Desay97Profile.Standard => "Standard",
        Desay97Profile.BenzPalm => "Benz Palm",
        _ => profile.ToString(),
    };
}
