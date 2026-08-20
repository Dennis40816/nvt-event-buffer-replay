using System.Globalization;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Avalonia.ViewModels;

public enum RawRegisterFilter
{
    All,
    Registers,
    ChangedReads,
    WritesAndCommands,
    Ambiguous,
}

public sealed record RawRegisterFilterChoice(string Label, RawRegisterFilter Filter)
{
    public override string ToString() => Label;
}

public sealed record RegisterProfileChoice(string Label, string? IcFamily)
{
    public override string ToString() => Label;
}

public sealed record RawRecordRow(SourceRecord Record, RegisterActivityEntry? Activity = null)
{
    public long Index => Record.Index;

    public string Timestamp => Record.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

    public string Operation => Record.Operation.ToString().ToUpperInvariant();

    public string Target => Record.Target;

    public string Address => Record.Address is { } address ? $"0x{address:X}" : "-";

    public string Register => Activity?.Register ?? Record.SourceFields?.GetValueOrDefault("register_readable") ?? "-";

    public string ByteCount => $"{Record.Data.Count}/{Record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    public string Preview => string.Join(' ', Record.Data.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return new[] { Index.ToString(CultureInfo.InvariantCulture), Timestamp, Operation, Target, Address, Register, Preview, Record.StableId }
            .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record DecodedFrameRow(
    int LogicalIndex,
    object Frame,
    SourceRecord Source,
    IReadOnlyList<SourceRecord> PhysicalRecords,
    byte Touches,
    bool CrcValid,
    bool AllBreak,
    bool HostStateEligible)
{
    public int Index => LogicalIndex + 1;

    public string Timestamp => Source.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

    public string Crc => CrcValid ? "OK" : "FAIL";

    public string State => AllBreak ? "ALL BREAK" : HostStateEligible ? "VALID" : "EVIDENCE";

    public string SourceId => Source.StableId;

    public static DecodedFrameRow FromCommon(int index, CommonEventBufferFrame frame) =>
        new(index, frame, frame.Source, [frame.Source], frame.NumTouches, frame.CrcValid, frame.AllBreak, frame.HostStateEligible);

    public static DecodedFrameRow FromDesay97(int index, Desay97Frame frame) =>
        new(index, frame, frame.Source, frame.Packet.PhysicalRecords, frame.NumTouches, frame.CrcValid, frame.AllBreak, frame.HostStateEligible);
}

public sealed record DiagnosticRow(ReplayDiagnostic Diagnostic)
{
    public string Code => Diagnostic.Code;

    public string Severity => Diagnostic.Severity.ToString().ToUpperInvariant();

    public string Message => Diagnostic.Message;
}

public sealed record ReviewGroupRow(ReviewEventGroup Group)
{
    public string Code => Group.Code;
    public string DisplayCode => EngineeringDisplayText.AddBreakOpportunities(Group.Code);
    public string Severity => Group.Severity.ToString().ToUpperInvariant();
    public string Message => Group.Message;
    public string Count => Group.Occurrences.Count == 1 ? "1 occurrence" : $"{Group.Occurrences.Count} occurrences";
    public string State => Group.AsilLifecycle?.ToString().ToUpperInvariant() ?? Group.WorkflowState.ToString().ToUpperInvariant();
    public string Disposition => Group.Disposition == ReviewDisposition.None ? string.Empty : Group.Disposition.ToString().ToUpperInvariant();
    public string? MarkerId => Group.Occurrences.FirstOrDefault()?.Diagnostic.Details?.GetValueOrDefault("marker_id");
    public bool CanUnmark => Group.Category == ReviewEventCategory.Annotation && MarkerId is not null;
}

public sealed record ReviewOccurrenceRow(int Number, ReviewOccurrence Occurrence)
{
    public string FrameAndSourceLabel => Occurrence.LogicalIndex is { } logicalIndex
        ? $"Frame {logicalIndex + 1} · source line {Occurrence.Diagnostic.Location.LineNumber}"
        : $"Physical source · line {Occurrence.Diagnostic.Location.LineNumber}";

    public override string ToString() => $"#{Number} · {FrameAndSourceLabel}";
}

public static class EngineeringDisplayText
{
    private const char ZeroWidthSpace = '\u200B';

    public static string AddBreakOpportunities(string value) => value
        .Replace("_", $"_{ZeroWidthSpace}", StringComparison.Ordinal)
        .Replace("=", $"={ZeroWidthSpace}", StringComparison.Ordinal);

    public static string FormatKeyValue(string key, string value, int maximumInlineLength = 30)
    {
        var pair = $"{key}={value}";
        return pair.Length <= maximumInlineLength
            ? AddBreakOpportunities(pair)
            : $"{AddBreakOpportunities(key)}=\n  {AddBreakOpportunities(value)}";
    }

    public static string RemoveBreakOpportunities(string value) => value.Replace(ZeroWidthSpace.ToString(), string.Empty, StringComparison.Ordinal);
}
