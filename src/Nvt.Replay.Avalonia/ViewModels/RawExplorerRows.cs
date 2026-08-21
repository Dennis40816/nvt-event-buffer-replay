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

            return "-";
        }
    }

    public string AddressSecondary => RawRegisterAddress;

    public bool HasSecondaryAddress => AddressSecondary != "-";

    public string I2cEndpoint => Record.I2c is { } transport
        ? $"0x{transport.SlaveAddress:X2} · {Direction}"
        : IsImplicitNdsTouchTarget
            ? $"0x01 · {Direction}"
            : $"— · {Direction}";

    public string RawRegisterAddress => TryGetRawRegisterOffset(out var offset)
        ? $"RAW REG 0x{offset:X2}"
        : "-";

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
            if (Record.I2c is { } transport)
                notes.Add($"I2C slave 0x{transport.SlaveAddress:X2} is the 7-bit address; {Direction} is shown separately.");
            else if (IsImplicitNdsTouchTarget)
                notes.Add($"NDS target TP maps to the fixed 7-bit I2C address 0x01; {Direction} is shown separately.");
            else
                notes.Add($"The source did not report a 7-bit I2C address; direction {Direction} is still available.");
            if (TryGetRawRegisterOffset(out var offset))
                notes.Add($"Raw register byte 0x{offset:X2} is preserved beside the resolved register address.");
            return notes.Count == 0 ? AddressPrimary : string.Join(Environment.NewLine, notes);
        }
    }

    public string Register => Activity?.Register ?? Field("register_readable") ?? Field("register_name") ?? "-";

    public string ByteCount => $"{Record.Data.Count}/{Record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    public string Preview => string.Join(' ', Record.Data.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return new[]
            {
                Index.ToString(CultureInfo.InvariantCulture), Timestamp, Operation, Target,
                I2cEndpoint, AddressPrimary, RawRegisterAddress, Field("raw_address") ?? string.Empty,
                Register, Preview, Record.StableId,
            }
            .Any(value => value.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private string Direction => Record.Operation switch
    {
        BusOperation.Read => "R",
        BusOperation.Write => "W",
        BusOperation.Paint => "P",
        _ => "-",
    };

    private string? Field(string key) =>
        RegisterAnnotation?.Fields.GetValueOrDefault(key) ?? Record.SourceFields?.GetValueOrDefault(key);

    private bool IsImplicitNdsTouchTarget =>
        Record.I2c is null && Record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase);

    private bool TryGetPageSwitch(out uint page)
    {
        page = 0;
        return Record.Operation == BusOperation.Write &&
            (Record.I2c is not null || Record.Target.Equals("TP", StringComparison.OrdinalIgnoreCase)) &&
            NvtRegisterTracker.TryGetPageSelection(Record.Data, out page);
    }

    private bool TryGetRawRegisterOffset(out byte offset)
    {
        if (Field("register_offset") is { Length: > 0 } text &&
            TryParseByte(text, out offset))
        {
            return true;
        }

        if (Record.I2c?.WriteCommands.LastOrDefault() is { Count: > 0 } command)
        {
            offset = command[0];
            return true;
        }

        if (Record.Operation == BusOperation.Write && Record.I2c is not null && Record.Data.Count > 0)
        {
            offset = Record.Data[0];
            return true;
        }

        offset = 0;
        return false;
    }

    private static bool TryParseByte(string value, out byte result)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result)
            : byte.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
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

public sealed record PaintMarkerRow(
    string MarkerId,
    int LogicalIndex,
    string FrameLabel,
    string TimeLabel);

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
