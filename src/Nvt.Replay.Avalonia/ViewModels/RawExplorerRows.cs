using System.Globalization;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Avalonia.ViewModels;

public sealed record RawRecordRow(SourceRecord Record)
{
    public long Index => Record.Index;

    public string Timestamp => Record.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";

    public string Operation => Record.Operation.ToString().ToUpperInvariant();

    public string Target => Record.Target;

    public string Address => Record.Address is { } address ? $"0x{address:X}" : "—";

    public string ByteCount => $"{Record.Data.Count}/{Record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "—"}";

    public string Preview => string.Join(' ', Record.Data.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
}

public sealed record DecodedFrameRow(int LogicalIndex, CommonEventBufferFrame Frame)
{
    public int Index => LogicalIndex + 1;

    public string Timestamp => Frame.Source.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";

    public byte Touches => Frame.NumTouches;

    public string Crc => Frame.CrcValid ? "OK" : "FAIL";

    public string State => Frame.AllBreak ? "ALL BREAK" : Frame.HostStateEligible ? "VALID" : "EVIDENCE";

    public string SourceId => Frame.Source.StableId;
}

public sealed record DiagnosticRow(ReplayDiagnostic Diagnostic)
{
    public string Code => Diagnostic.Code;

    public string Severity => Diagnostic.Severity.ToString().ToUpperInvariant();

    public string Message => Diagnostic.Message;
}
