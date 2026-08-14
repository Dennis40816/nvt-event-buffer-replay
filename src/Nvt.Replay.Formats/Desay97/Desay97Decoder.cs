using System.Globalization;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Formats.Desay97;

public sealed class Desay97Decoder
{
    public Desay97DecodeResult Decode(Desay97AssembledPacket packet, Desay97Profile profile)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var diagnostics = new List<ReplayDiagnostic>();
        var data = packet.Data;
        if (data.Count < 2)
        {
            diagnostics.Add(Diagnostic(packet, "DESAY97_LENGTH_MISMATCH", "Desay 0x97 requires at least a header and CRC byte."));
            return new Desay97DecodeResult(null, diagnostics);
        }

        var touchCount = (byte)(data[0] & 0x0F);
        var expectedLength = 1 + (5 * touchCount) + 1;
        if (data.Count != expectedLength)
        {
            diagnostics.Add(Diagnostic(
                packet,
                "DESAY97_LENGTH_MISMATCH",
                $"Desay 0x97 touch count {touchCount} requires {expectedLength} bytes; captured {data.Count}.",
                new Dictionary<string, string>
                {
                    ["expected"] = expectedLength.ToString(CultureInfo.InvariantCulture),
                    ["actual"] = data.Count.ToString(CultureInfo.InvariantCulture),
                }));
            return new Desay97DecodeResult(null, diagnostics);
        }

        var computedCrc = Crc8Poly1D.Compute(data.Take(data.Count - 1).ToArray());
        var crcValid = computedCrc == data[^1];
        if (!crcValid)
        {
            diagnostics.Add(Diagnostic(
                packet,
                "DESAY97_CRC_MISMATCH",
                $"Desay 0x97 CRC mismatch: expected 0x{computedCrc:X2}, captured 0x{data[^1]:X2}."));
        }

        var fingers = new Desay97Finger[touchCount];
        for (var index = 0; index < touchCount; index++)
        {
            var offset = 1 + (index * 5);
            var meta = data[offset];
            var profileFlag = (meta & 0x80) != 0;
            fingers[index] = new Desay97Finger(
                meta,
                (byte)(meta & 0x0F),
                (TouchStatus)((meta >> 5) & 0x03),
                ReadUInt16BigEndian(data, offset + 1),
                ReadUInt16BigEndian(data, offset + 3),
                profile == Desay97Profile.Standard && profileFlag,
                profile == Desay97Profile.BenzPalm && profileFlag,
                (meta & 0x10) != 0);
        }

        var header = data[0];
        foreach (var alarm in HeaderAlarms(header))
        {
            diagnostics.Add(Diagnostic(
                packet,
                alarm.Code,
                alarm.Message,
                severity: DiagnosticSeverity.Alarm));
        }
        var frame = new Desay97Frame(
            profile,
            packet,
            header,
            (header & 0x80) != 0,
            (header & 0x40) != 0,
            (header & 0x20) != 0,
            (header & 0x10) != 0,
            touchCount,
            fingers,
            touchCount == 0,
            data[^1],
            computedCrc,
            crcValid,
            crcValid);
        return new Desay97DecodeResult(frame, diagnostics);
    }

    private static ushort ReadUInt16BigEndian(IReadOnlyList<byte> data, int offset) =>
        (ushort)((data[offset] << 8) | data[offset + 1]);

    private static ReplayDiagnostic Diagnostic(
        Desay97AssembledPacket packet,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null,
        DiagnosticSeverity severity = DiagnosticSeverity.Warning) =>
        new(severity, code, message, packet.StableId, packet.PayloadRead.Location, details);

    private static IEnumerable<(string Code, string Message)> HeaderAlarms(byte header)
    {
        if ((header & 0x40) != 0) yield return ("DESAY97_SHORT_ALARM", "Desay 0x97 header asserted the Short alarm.");
        if ((header & 0x20) != 0) yield return ("DESAY97_OPEN_ALARM", "Desay 0x97 header asserted the Open alarm.");
        if ((header & 0x10) != 0) yield return ("DESAY97_TP_ASIL_ALARM", "Desay 0x97 header asserted TP ASIL Error.");
    }
}
