using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
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

public sealed class RawRecordRow : INotifyPropertyChanged
{
    private bool isExpanded;

    public RawRecordRow(
        SourceRecord record,
        RegisterActivityEntry? activity = null,
        RegisterAnnotation? registerAnnotation = null)
    {
        Record = record;
        Activity = activity;
        RegisterAnnotation = registerAnnotation;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SourceRecord Record { get; }

    public RegisterActivityEntry? Activity { get; }

    public RegisterAnnotation? RegisterAnnotation { get; }

    public bool IsExpanded
    {
        get => isExpanded;
        private set
        {
            if (isExpanded == value) return;
            isExpanded = value;
            OnPropertyChanged();
        }
    }

    public RawRecordDetail? Detail { get; private set; }

    public long Index => Record.Index;

    public string Timestamp => Record.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

    public string Operation => Record.Operation.ToString().ToUpperInvariant();

    public string Target => Record.Target;

    public string Address => AddressPrimary;

    public string AddressPrimary
    {
        get
        {
            if (IsNdsEventBufferRead) return "EVENT BUFFER";

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
        : $"— · {Direction}";

    public string RawRegisterAddress
    {
        get
        {
            if (IsNdsEventBufferRead) return "RAW REG —";
            if (IsNdsAbsoluteRegisterAddress && Field("raw_address") is { Length: > 0 } rawAddress)
                return $"RAW ADDR {rawAddress}";
            return TryGetRawRegisterOffset(out var offset)
                ? $"RAW REG 0x{offset:X2}"
                : "-";
        }
    }

    public string AddressTooltip
    {
        get
        {
            var notes = new List<string>();
            if (IsNdsEventBufferRead)
            {
                notes.Add($"NDS Paint is an Event Buffer read from 7-bit I2C slave 0x{Record.I2c!.SlaveAddress:X2}; the source does not report a register address.");
            }
            else if (TryGetPageSwitch(out var page))
                notes.Add($"Switch page selects 0x{page:X}; the captured FF command bytes remain unchanged.");
            else if (Field("register_profile_resolution") == "inferred")
                notes.Add("Register address is inferred from the confirmed IC profile and Event Buffer offset; the source has no page-select transaction.");
            else if (Field("register_page_known") == "false")
                notes.Add("The source has no page-select transaction; only the register offset is captured.");
            if (Record.I2c is { } transport)
                notes.Add($"I2C slave 0x{transport.SlaveAddress:X2} is the 7-bit address; {Direction} is shown separately.");
            else if (IsNdsAbsoluteRegisterAddress)
                notes.Add($"This NDS field is an absolute register address; the source does not report a 7-bit I2C address for this record. Direction {Direction} is shown separately.");
            else
                notes.Add($"The source did not report a 7-bit I2C address; direction {Direction} is still available.");
            if (IsNdsAbsoluteRegisterAddress && Field("raw_address") is { Length: > 0 } rawAddress)
                notes.Add($"Raw NDS address token {rawAddress} is preserved beside the resolved register meaning.");
            else if (TryGetRawRegisterOffset(out var offset))
                notes.Add($"Raw register byte 0x{offset:X2} is preserved beside the resolved register address.");
            return notes.Count == 0 ? AddressPrimary : string.Join(Environment.NewLine, notes);
        }
    }

    public string Register => Activity?.Register ?? Field("register_readable") ?? Field("register_name") ??
        (IsNdsEventBufferRead ? "Event Buffer frame" : "-");

    public string ByteCount => $"{Record.Data.Count}/{Record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    public string Preview => string.Join(' ', Record.Data.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    public void SetExpanded(bool expanded)
    {
        if (expanded && Detail is null)
        {
            Detail = BuildDetail();
            OnPropertyChanged(nameof(Detail));
        }
        IsExpanded = expanded;
    }

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
        BusOperation.Paint when IsNdsRecord => "R",
        BusOperation.Paint => "P",
        _ => "-",
    };

    private string? Field(string key) =>
        RegisterAnnotation?.Fields.GetValueOrDefault(key) ?? Record.SourceFields?.GetValueOrDefault(key);

    private bool IsNdsAbsoluteRegisterAddress =>
        Record.SourceFields?.GetValueOrDefault(NdsCommunicationLogAdapter.AddressSemanticsField) ==
        NdsCommunicationLogAdapter.AbsoluteRegisterAddress;

    private bool IsNdsEventBufferRead =>
        Record.SourceFields?.GetValueOrDefault(NdsCommunicationLogAdapter.AccessSemanticsField) ==
        NdsCommunicationLogAdapter.EventBufferRead;

    private bool IsNdsRecord =>
        Record.SourceFields?.GetValueOrDefault("adapter") == NdsCommunicationLogAdapter.AdapterId;

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

    private RawRecordDetail BuildDetail()
    {
        var transaction = new List<RawDetailField>
        {
            new("Operation", $"{Operation} · {Target}"),
            new("I²C endpoint", I2cEndpoint),
            new("Byte count", Record.DeclaredByteCount is { } declared
                ? $"{Record.Data.Count} captured · {declared} declared"
                : $"{Record.Data.Count} captured · declared unknown"),
        };
        if (Record.I2c is { } i2c)
        {
            transaction.Add(new RawDetailField("Address ACK", i2c.AddressAcknowledged switch
            {
                true => "ACK",
                false => "NAK",
                null => "Not reported",
            }));
            transaction.Add(new RawDetailField("Data ACK", I2cAckSummary.Format(i2c.Acked)));
            transaction.Add(new RawDetailField(
                "Write context",
                i2c.WriteCommands.Count == 0
                    ? "None"
                    : string.Join(" | ", i2c.WriteCommands.Select(FormatBytes))));
            if (!string.IsNullOrWhiteSpace(i2c.Error))
                transaction.Add(new RawDetailField("Transport error", i2c.Error));
        }

        var register = new List<RawDetailField>
        {
            new("Resolved", AddressPrimary),
            new("Raw evidence", RawRegisterAddress),
            new("Interpretation", DetailRegisterInterpretation()),
        };
        if (TryGetPageSwitch(out var page))
            register.Add(new RawDetailField("Switch page", $"0x{page:X}"));
        if (Field("register_profile") is { Length: > 0 } profile)
            register.Add(new RawDetailField("IC profile", profile));
        if (Field("register_profile_resolution") is { Length: > 0 } resolution)
            register.Add(new RawDetailField("Resolution", resolution));
        if (Field("fw_command_name") is { Length: > 0 } commandName)
        {
            var commandCode = Field("fw_command_code");
            register.Add(new RawDetailField(
                "FW command",
                string.IsNullOrWhiteSpace(commandCode) ? commandName : $"{commandCode} · {commandName}"));
        }

        var source = new List<RawDetailField>
        {
            new("Adapter", Field("adapter") ?? "Not reported"),
            new("Location", $"Line {Record.Location.LineNumber:N0} · byte {Record.Location.ByteOffset:N0}"),
            new("Stable ID", Record.StableId),
        };
        if (Field("dialect") is { Length: > 0 } dialect)
            source.Add(new RawDetailField("Dialect", dialect));
        if (Field("packet_id") is { Length: > 0 } packetId)
            source.Add(new RawDetailField("Packet", packetId));

        return new RawRecordDetail(
            transaction,
            register,
            source,
            FormatHexDump(Record.Data),
            string.IsNullOrWhiteSpace(Record.RawText) ? "Source text not preserved." : Record.RawText);
    }

    private string DetailRegisterInterpretation()
    {
        if (TryGetPageSwitch(out var page)) return $"Switch page · 0x{page:X}";
        var name = Field("register_display_name") ?? Field("register_name");
        var meaning = Field("register_meaning");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return string.IsNullOrWhiteSpace(meaning) || meaning.Equals("raw_only", StringComparison.OrdinalIgnoreCase)
                ? name.Replace('_', ' ')
                : $"{name.Replace('_', ' ')} · {meaning}";
        }
        return IsNdsEventBufferRead ? "Event Buffer frame" : Register;
    }

    private static string FormatBytes(IReadOnlyList<byte> values) =>
        values.Count == 0
            ? "—"
            : string.Join(' ', values.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static string FormatHexDump(IReadOnlyList<byte> values)
    {
        if (values.Count == 0) return "—";
        var result = new StringBuilder(values.Count * 3 + (values.Count / 16 + 1) * 7);
        for (var offset = 0; offset < values.Count; offset += 16)
        {
            if (result.Length > 0) result.AppendLine();
            result.Append(offset.ToString("X4", CultureInfo.InvariantCulture));
            result.Append("  ");
            var end = Math.Min(offset + 16, values.Count);
            for (var index = offset; index < end; index++)
            {
                if (index > offset) result.Append(' ');
                result.Append(values[index].ToString("X2", CultureInfo.InvariantCulture));
            }
        }
        return result.ToString();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static bool TryParseByte(string value, out byte result)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? byte.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result)
            : byte.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}

public sealed record RawDetailField(string Label, string Value);

public sealed record RawRecordDetail(
    IReadOnlyList<RawDetailField> TransactionFields,
    IReadOnlyList<RawDetailField> RegisterFields,
    IReadOnlyList<RawDetailField> SourceFields,
    string FullPayload,
    string SourceText);

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
