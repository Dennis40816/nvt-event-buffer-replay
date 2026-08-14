using System.Globalization;
using Nvt.Replay.Core;

namespace Nvt.Replay.Formats.Common;

public sealed class CommonStreamMonitor
{
    private (bool TpAsilError, byte AsilRaw)? previousAsil;
    private (byte NoResponse, byte Busy)? previousBus;

    public IReadOnlyList<ReplayDiagnostic> Observe(CommonEventBufferFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var diagnostics = new List<ReplayDiagnostic>();
        var currentAsil = (frame.TpAsilError, frame.Asil.Raw);
        var hasAlarm = frame.TpAsilError || frame.Asil.Alarm;
        var previousHadAlarm = previousAsil is { } old && (old.TpAsilError || (old.AsilRaw & 0x1F) != 0);

        if (hasAlarm && previousAsil != currentAsil)
        {
            diagnostics.Add(CommonEventBufferDecoder.Diagnostic(
                frame.Source,
                DiagnosticSeverity.Alarm,
                "COMMON_ASIL_ALARM",
                "Common event buffer reports an ASIL alarm.",
                new Dictionary<string, string>
                {
                    ["tp_asil_err"] = frame.TpAsilError.ToString(CultureInfo.InvariantCulture),
                    ["asil_byte_raw"] = $"0x{frame.Asil.Raw:X2}",
                    ["short"] = frame.Asil.Short.ToString(CultureInfo.InvariantCulture),
                    ["open"] = frame.Asil.Open.ToString(CultureInfo.InvariantCulture),
                    ["error_code"] = ((byte)frame.Asil.ErrorCode).ToString(CultureInfo.InvariantCulture),
                }));
        }
        else if (previousHadAlarm && !hasAlarm)
        {
            diagnostics.Add(CommonEventBufferDecoder.Diagnostic(
                frame.Source,
                DiagnosticSeverity.Info,
                "COMMON_ASIL_CLEARED",
                "Common event-buffer ASIL fields returned to zero."));
        }

        previousAsil = currentAsil;

        var hasReportedBreak = frame.Fingers.Any(finger =>
            finger.Status == TouchStatus.Break && !IsAllFfSlot(finger));
        if (frame.NumTouches == 0 && !frame.AllBreak && !hasReportedBreak)
        {
            diagnostics.Add(CommonEventBufferDecoder.Diagnostic(
                frame.Source,
                DiagnosticSeverity.Warning,
                "COMMON_ALL_BREAK_PAYLOAD_MISMATCH",
                "NumTouches is zero but not every common finger byte is 0xFF."));
        }

        if (frame.DiagnosticPacket?.PalmOn == true)
        {
            var reportedPalm = frame.Fingers.Any(finger => finger.Type == TouchType.Palm);
            if (!frame.AllBreak || reportedPalm)
            {
                diagnostics.Add(CommonEventBufferDecoder.Diagnostic(
                    frame.Source,
                    DiagnosticSeverity.Warning,
                    "COMMON_GLOBAL_PALM_INCONSISTENT",
                    "Global Palm On expects All Break and no coordinate-reported Palm.",
                    new Dictionary<string, string>
                    {
                        ["all_break"] = frame.AllBreak.ToString(CultureInfo.InvariantCulture),
                        ["reported_palm"] = reportedPalm.ToString(CultureInfo.InvariantCulture),
                    }));
            }
        }

        if (frame.BusStatus is { } bus)
        {
            var currentBus = (bus.NoResponseCounter, bus.BusyCounter);
            if (previousBus is { } oldBus)
            {
                AddCounterIncrease(diagnostics, frame, "no_response", oldBus.NoResponse, currentBus.NoResponseCounter);
                AddCounterIncrease(diagnostics, frame, "busy", oldBus.Busy, currentBus.BusyCounter);
            }

            previousBus = currentBus;
        }

        return diagnostics;
    }

    private static bool IsAllFfSlot(CommonFinger finger) =>
        finger.RawMeta == 0xFF &&
        finger.X == 0xFFFF &&
        finger.Y == 0xFFFF &&
        finger.Reserved.All(value => value == 0xFF);

    private static void AddCounterIncrease(
        ICollection<ReplayDiagnostic> diagnostics,
        CommonEventBufferFrame frame,
        string name,
        byte previous,
        byte current)
    {
        if (current <= previous)
        {
            return;
        }

        diagnostics.Add(CommonEventBufferDecoder.Diagnostic(
            frame.Source,
            DiagnosticSeverity.Warning,
            "COMMON_BUS_COUNTER_INCREASED",
            $"Common bus {name} counter increased from {previous} to {current}.",
            new Dictionary<string, string>
            {
                ["counter"] = name,
                ["previous"] = previous.ToString(CultureInfo.InvariantCulture),
                ["current"] = current.ToString(CultureInfo.InvariantCulture),
            }));
    }
}
