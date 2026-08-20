using System.Globalization;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Sources;

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

public sealed record RawRecordRow(
    SourceRecord Record,
    RegisterActivityEntry? Activity = null,
    RegisterAnnotation? RegisterAnnotation = null)
{
    public long Index => Record.Index;

    public string Timestamp => Record.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

    public string Operation => Record.Operation.ToString().ToUpperInvariant();

    public string Target => Record.Target;

    public string Address => AddressPrimary;

    public string AddressPrimary
    {
        get
        {
            if (TryGetPageSwitch(out var page))
                return $"PAGE 0x{page:X}";

            var registerAddress = Field("register_address");
            if (!string.IsNullOrWhiteSpace(registerAddress) &&
                !registerAddress.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                return $"REG {registerAddress}";
            }
            if (Record.Address is { } address) return $"REG 0x{address:X}";

            var offset = Field("register_offset");
            if (!string.IsNullOrWhiteSpace(offset)) return $"PTR +{offset}";
            if (Record.Operation == BusOperation.Write && Record.I2c is not null && Record.Data.Count == 1)
                return $"PTR +0x{Record.Data[0]:X2}";

            return BusAddress ?? "-";
        }
    }

    public string AddressSecondary => AddressPrimary.StartsWith("I2C ", StringComparison.Ordinal)
        ? string.Empty
        : BusAddress ?? string.Empty;

    public bool HasSecondaryAddress => AddressSecondary.Length > 0;

    public string AddressTooltip
    {
        get
        {
            var notes = new List<string>();
            if (TryGetPageSwitch(out var page))
                notes.Add($"Switch page selects 0x{page:X}; the captured FF command bytes remain unchanged.");
            else if (Field("register_profile_resolution") == "inferred")
                notes.Add("Register address is inferred from the confirmed IC profile and Event Buffer offset; the source has no page-select transaction.");
            else if (Field("register_page_known") == "false")
                notes.Add("The source has no page-select transaction; only the register offset is captured.");
            if (BusAddress is not null)
                notes.Add("I2C shows the captured 8-bit bus address including the read/write bit.");
            return notes.Count == 0 ? AddressPrimary : string.Join(Environment.NewLine, notes);
        }
    }

    public string Register => Activity?.Register ?? Field("register_readable") ?? Field("register_name") ?? "-";

    public string ByteCount => $"{Record.Data.Count}/{Record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    public string Preview => string.Join(' ', Record.Data.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return new[] { Index.ToString(CultureInfo.InvariantCulture), Timestamp, Operation, Target, AddressPrimary, AddressSecondary, Register, Preview, Record.StableId }
            .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private string? BusAddress
    {
        get
        {
            if (Field("raw_address") is { Length: > 0 } rawAddress)
                return $"I2C {rawAddress} {Direction}";
            if (Record.I2c is not { } transport) return null;
            var raw = (transport.SlaveAddress << 1) | (Record.Operation == BusOperation.Read ? 1 : 0);
            return $"I2C 0x{raw:X2} {Direction}";
        }
    }

    private string Direction => Record.Operation switch
    {
        BusOperation.Read => "R",
        BusOperation.Write => "W",
        _ => string.Empty,
    };

    private string? Field(string key) =>
        RegisterAnnotation?.Fields.GetValueOrDefault(key) ?? Record.SourceFields?.GetValueOrDefault(key);

    private bool TryGetPageSwitch(out uint page)
    {
        page = 0;
        return Record.Operation == BusOperation.Write &&
            Record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase) &&
            NvtRegisterTracker.TryGetPageSelection(Record.Data, out page);
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
