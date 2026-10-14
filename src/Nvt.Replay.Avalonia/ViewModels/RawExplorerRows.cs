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

/// <summary>Specifies which raw records are included by the register activity filter.</summary>
public enum RawRegisterFilter
{
    /// <summary>Includes all raw records.</summary>
    All,
    /// <summary>Includes raw records with register activity.</summary>
    Registers,
    /// <summary>Includes records whose register activity changed from the previous sample.</summary>
    ChangedReads,
    /// <summary>Includes register writes, commands, resets, and page switches.</summary>
    WritesAndCommands,
    /// <summary>Includes records whose register activity is marked ambiguous.</summary>
    Ambiguous,
}

/// <summary>Pairs a display label with a raw register filter choice.</summary>
/// <param name="Label">The label displayed for the filter choice.</param>
/// <param name="Filter">The filter applied when this choice is selected.</param>
public sealed record RawRegisterFilterChoice(string Label, RawRegisterFilter Filter)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>Pairs a display label with an optional IC family for register interpretation.</summary>
/// <param name="Label">The label displayed for the profile choice.</param>
/// <param name="IcFamily">The selected IC family, or null when no family is selected.</param>
public sealed record RegisterProfileChoice(string Label, string? IcFamily)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>Exposes a source record and its register interpretation for raw-record display and search.</summary>
public sealed class RawRecordRow : INotifyPropertyChanged
{
    private bool isExpanded;

    /// <summary>Creates a display row for a source record with optional register activity and annotation.</summary>
    public RawRecordRow(
        SourceRecord record,
        RegisterActivityEntry? activity = null,
        RegisterAnnotation? registerAnnotation = null)
    {
        Record = record;
        Activity = activity;
        RegisterAnnotation = registerAnnotation;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Gets the source record represented by this row.</summary>
    public SourceRecord Record { get; }

    /// <summary>Gets the register activity associated with the source record, if available.</summary>
    public RegisterActivityEntry? Activity { get; }

    /// <summary>Gets the register annotation used to interpret the source record's fields, if available.</summary>
    public RegisterAnnotation? RegisterAnnotation { get; }

    /// <summary>Gets whether the row's detailed view is expanded.</summary>
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

    /// <summary>Gets the record's detailed display data after its first expansion.</summary>
    public RawRecordDetail? Detail { get; private set; }

    /// <summary>Gets the index of the underlying source record.</summary>
    public long Index => Record.Index;

    /// <summary>Gets the navigation label containing the source record's line number.</summary>
    public string SourceNavigationLabel => $"Open source line {Record.Location.LineNumber:N0}";

    /// <summary>Gets the recorded time formatted as HH:mm:ss.fff, or a dash when unavailable.</summary>
    public string Timestamp => Record.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Gets the source record's bus operation as uppercase text.</summary>
    public string Operation => Record.Operation.ToString().ToUpperInvariant();

    /// <summary>Gets the target reported by the source record.</summary>
    public string Target => Record.Target;

    /// <summary>Gets the primary address label used to display this record.</summary>
    public string Address => AddressPrimary;

    /// <summary>Gets the Event Buffer, page, register, or pointer label derived from the record and annotation.</summary>
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

    /// <summary>Gets the raw register address label displayed beside the primary address.</summary>
    public string AddressSecondary => RawRegisterAddress;

    /// <summary>Gets whether the secondary address label contains more than the unavailable-value dash.</summary>
    public bool HasSecondaryAddress => AddressSecondary != "-";

    /// <summary>Gets the I2C slave address and operation direction, using a dash when the address is unavailable.</summary>
    public string I2cEndpoint => Record.I2c is { } transport
        ? $"0x{transport.SlaveAddress:X2} · {Direction}"
        : $"— · {Direction}";

    /// <summary>Gets a label for the raw NDS address or register offset, or an unavailable-value indicator.</summary>
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

    /// <summary>Gets explanatory text about the address interpretation, transport endpoint, and preserved address evidence.</summary>
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

    /// <summary>Gets the register activity label or annotated register name, with an Event Buffer or dash fallback.</summary>
    public string Register => Activity?.Register ?? Field("register_readable") ?? Field("register_name") ??
        (IsNdsEventBufferRead ? "Event Buffer frame" : "-");

    /// <summary>Gets the captured and declared byte counts separated by a slash, using a dash for an unknown declared count.</summary>
    public string ByteCount => $"{Record.Data.Count}/{Record.DeclaredByteCount?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    /// <summary>Gets a space-separated hexadecimal preview of the first sixteen captured bytes.</summary>
    public string Preview => string.Join(' ', Record.Data.Take(16).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>Sets the expansion state and creates the record's detail data on its first expansion.</summary>
    public void SetExpanded(bool expanded)
    {
        if (expanded && Detail is null)
        {
            Detail = BuildDetail();
            OnPropertyChanged(nameof(Detail));
        }
        IsExpanded = expanded;
    }

    /// <summary>Checks searchable row fields and the stable record ID for a trimmed, case-insensitive query.</summary>
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

/// <summary>Stores a labeled value in a raw record's detailed display.</summary>
/// <param name="Label">The name displayed for the detail field.</param>
/// <param name="Value">The text displayed as the field's value.</param>
public sealed record RawDetailField(string Label, string Value);

/// <summary>Stores transaction, register, source, and payload details for an expanded raw record.</summary>
/// <param name="TransactionFields">The labeled bus operation and transport details.</param>
/// <param name="RegisterFields">The labeled register interpretation and address evidence.</param>
/// <param name="SourceFields">The labeled source adapter, location, and identity details.</param>
/// <param name="FullPayload">The captured payload formatted as an offset-based hexadecimal dump.</param>
/// <param name="SourceText">The preserved source text or a message indicating it was not preserved.</param>
public sealed record RawRecordDetail(
    IReadOnlyList<RawDetailField> TransactionFields,
    IReadOnlyList<RawDetailField> RegisterFields,
    IReadOnlyList<RawDetailField> SourceFields,
    string FullPayload,
    string SourceText);

/// <summary>Exposes a decoded frame, its source records, and replay eligibility as a display row.</summary>
/// <param name="LogicalIndex">The zero-based index of the decoded frame.</param>
/// <param name="Frame">The decoded protocol frame represented by the row.</param>
/// <param name="Source">The primary source record for the decoded frame.</param>
/// <param name="PhysicalRecords">The physical source records contributing to the decoded frame.</param>
/// <param name="Touches">The number of touches reported by the decoded frame.</param>
/// <param name="CrcValid">Whether the decoded frame passed CRC validation.</param>
/// <param name="AllBreak">Whether the decoded frame indicates an all-break state.</param>
/// <param name="HostStateEligible">Whether the decoded frame is eligible for host-state replay.</param>
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
    /// <summary>Gets the one-based frame number displayed for the logical index.</summary>
    public int Index => LogicalIndex + 1;

    /// <summary>Gets the primary source record's time as HH:mm:ss.fff, or a dash when unavailable.</summary>
    public string Timestamp => Source.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

    /// <summary>Gets OK or FAIL according to the decoded frame's CRC validity.</summary>
    public string Crc => CrcValid ? "OK" : "FAIL";

    /// <summary>Gets ALL BREAK, VALID, or EVIDENCE according to the frame's break state and host-state eligibility.</summary>
    public string State => AllBreak ? "ALL BREAK" : HostStateEligible ? "VALID" : "EVIDENCE";

    /// <summary>Gets the stable ID of the decoded frame's primary source record.</summary>
    public string SourceId => Source.StableId;

    /// <summary>Creates a display row from a Common frame using its source record as the sole physical record.</summary>
    public static DecodedFrameRow FromCommon(int index, CommonEventBufferFrame frame) =>
        new(index, frame, frame.Source, [frame.Source], frame.NumTouches, frame.CrcValid, frame.AllBreak, frame.HostStateEligible);

    /// <summary>Creates a display row from a Desay 0x97 frame and its packet's physical source records.</summary>
    public static DecodedFrameRow FromDesay97(int index, Desay97Frame frame) =>
        new(index, frame, frame.Source, frame.Packet.PhysicalRecords, frame.NumTouches, frame.CrcValid, frame.AllBreak, frame.HostStateEligible);
}

/// <summary>Exposes a replay diagnostic's code, severity, and message for display.</summary>
/// <param name="Diagnostic">The replay diagnostic represented by the row.</param>
public sealed record DiagnosticRow(ReplayDiagnostic Diagnostic)
{
    /// <summary>Gets the code identifying the replay diagnostic.</summary>
    public string Code => Diagnostic.Code;

    /// <summary>Gets the replay diagnostic's severity as uppercase text.</summary>
    public string Severity => Diagnostic.Severity.ToString().ToUpperInvariant();

    /// <summary>Gets the explanatory message from the replay diagnostic.</summary>
    public string Message => Diagnostic.Message;
}

/// <summary>Exposes a review event group's occurrences, state, disposition, and marker information for display.</summary>
/// <param name="Group">The review event group represented by the row.</param>
public sealed record ReviewGroupRow(ReviewEventGroup Group)
{
    /// <summary>Gets the code identifying the review event group.</summary>
    public string Code => Group.Code;
    /// <summary>Gets the group code with zero-width spaces after underscores and equals signs for wrapping.</summary>
    public string DisplayCode => EngineeringDisplayText.AddBreakOpportunities(Group.Code);
    /// <summary>Gets the review event group's severity as uppercase text.</summary>
    public string Severity => Group.Severity.ToString().ToUpperInvariant();
    /// <summary>Gets the explanatory message from the review event group.</summary>
    public string Message => Group.Message;
    /// <summary>Gets the group's occurrence count with singular or plural wording.</summary>
    public string Count => Group.Occurrences.Count == 1 ? "1 occurrence" : $"{Group.Occurrences.Count} occurrences";
    /// <summary>Gets the group's ASIL lifecycle or, when absent, its workflow state as uppercase text.</summary>
    public string State => Group.AsilLifecycle?.ToString().ToUpperInvariant() ?? Group.WorkflowState.ToString().ToUpperInvariant();
    /// <summary>Gets the group's disposition as uppercase text, or an empty string for no disposition.</summary>
    public string Disposition => Group.Disposition == ReviewDisposition.None ? string.Empty : Group.Disposition.ToString().ToUpperInvariant();
    /// <summary>Gets the marker ID from the first occurrence's diagnostic details, if present.</summary>
    public string? MarkerId => Group.Occurrences.FirstOrDefault()?.Diagnostic.Details?.GetValueOrDefault("marker_id");
    /// <summary>Gets whether the group is an annotation with a marker ID available for unmarking.</summary>
    public bool CanUnmark => Group.Category == ReviewEventCategory.Annotation && MarkerId is not null;
}

/// <summary>Pairs a review occurrence with its display number and frame or physical-source location.</summary>
/// <param name="Number">The number displayed before the occurrence's frame and source label.</param>
/// <param name="Occurrence">The review occurrence represented by the row.</param>
public sealed record ReviewOccurrenceRow(int Number, ReviewOccurrence Occurrence)
{
    /// <summary>Gets the one-based frame number and source line, or the physical source line when no logical index exists.</summary>
    public string FrameAndSourceLabel => Occurrence.LogicalIndex is { } logicalIndex
        ? $"Frame {logicalIndex + 1} · source line {Occurrence.Diagnostic.Location.LineNumber}"
        : $"Physical source · line {Occurrence.Diagnostic.Location.LineNumber}";

    /// <inheritdoc />
    public override string ToString() => $"#{Number} · {FrameAndSourceLabel}";
}

/// <summary>Stores a Paint marker's identity, logical index, and displayed frame and time labels.</summary>
/// <param name="MarkerId">The identifier of the Paint marker.</param>
/// <param name="LogicalIndex">The zero-based logical index at the marker's start.</param>
/// <param name="FrameLabel">The display label for the marker's starting frame.</param>
/// <param name="TimeLabel">The display label for the marker's starting time.</param>
public sealed record PaintMarkerRow(
    string MarkerId,
    int LogicalIndex,
    string FrameLabel,
    string TimeLabel);

/// <summary>Formats engineering text with removable wrapping opportunities at underscores and equals signs.</summary>
public static class EngineeringDisplayText
{
    private const char ZeroWidthSpace = '\u200B';

    /// <summary>Inserts zero-width spaces after underscores and equals signs to allow text wrapping.</summary>
    public static string AddBreakOpportunities(string value) => value
        .Replace("_", $"_{ZeroWidthSpace}", StringComparison.Ordinal)
        .Replace("=", $"={ZeroWidthSpace}", StringComparison.Ordinal);

    /// <summary>Formats a key-value pair with wrapping opportunities and moves long values to an indented line.</summary>
    public static string FormatKeyValue(string key, string value, int maximumInlineLength = 30)
    {
        var pair = $"{key}={value}";
        return pair.Length <= maximumInlineLength
            ? AddBreakOpportunities(pair)
            : $"{AddBreakOpportunities(key)}=\n  {AddBreakOpportunities(value)}";
    }

    /// <summary>Removes all zero-width spaces from the supplied display text.</summary>
    public static string RemoveBreakOpportunities(string value) => value.Replace(ZeroWidthSpace.ToString(), string.Empty, StringComparison.Ordinal);
}
