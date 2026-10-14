using System.Globalization;
using System.Text;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Holds a contact's identity, touch state, coordinates, and source for an inspector row.</summary>
/// <param name="Id">The contact identifier.</param>
/// <param name="Type">The touch type reported for the contact.</param>
/// <param name="Status">The touch status reported for the contact.</param>
/// <param name="X">The contact's X coordinate.</param>
/// <param name="Y">The contact's Y coordinate.</param>
/// <param name="Invalid">Whether the contact is marked invalid.</param>
/// <param name="ReportedThisFrame">Whether the row uses a contact reported by the current frame.</param>
public sealed record InspectorContactRow(
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    bool Invalid,
    bool ReportedThisFrame)
{
    /// <summary>Gets the contact identifier prefixed with ID for display.</summary>
    public string IdLabel => $"ID {Id}";
    /// <summary>Gets the finger, glove, palm, or other label for the contact's touch type.</summary>
    public string TypeLabel => Type switch
    {
        TouchType.Finger => "Finger",
        TouchType.Glove => "Glove",
        TouchType.Palm => "Palm",
        _ => "Other",
    };
    /// <summary>Gets the invalid, touch status, palm, or idle label for the contact's state.</summary>
    public string StateLabel => Invalid ? "INVALID" : Status switch
    {
        TouchStatus.Enter => "ENTER",
        TouchStatus.Move => "MOVE",
        TouchStatus.Break => "BREAK",
        _ when Type == TouchType.Palm => "PALM",
        _ => "IDLE",
    };
    /// <summary>Gets the contact's X coordinate formatted using the invariant culture.</summary>
    public string XLabel => X.ToString(CultureInfo.InvariantCulture);
    /// <summary>Gets the contact's Y coordinate formatted using the invariant culture.</summary>
    public string YLabel => Y.ToString(CultureInfo.InvariantCulture);
    /// <summary>Gets REPORT for a contact from the current frame or HOST for a host-state contact.</summary>
    public string SourceLabel => ReportedThisFrame ? "REPORT" : "HOST";
    /// <summary>Gets the contact's identity, type, state, coordinates, and source as one accessible label.</summary>
    public string AccessibleLabel => $"{IdLabel}, {TypeLabel}, {StateLabel}, X {XLabel}, Y {YLabel}, {SourceLabel}";
}

/// <summary>Holds active host contact counts by type, including a global palm indication.</summary>
/// <param name="Fingers">The active finger contact count.</param>
/// <param name="Gloves">The active glove contact count.</param>
/// <param name="Palms">The active palm count, or one when only a global palm is indicated.</param>
/// <param name="Other">The active contact count for the reserved touch type.</param>
public sealed record InspectorContactSummary(int Fingers, int Gloves, int Palms, int Other)
{
    /// <summary>Gets the sum of the finger, glove, palm, and reserved-type contact counts.</summary>
    public int Total => Fingers + Gloves + Palms + Other;
}

/// <summary>Holds a label and value for a protocol or register detail in the inspector.</summary>
/// <param name="Label">The name of the displayed detail.</param>
/// <param name="Value">The text displayed for the detail's value.</param>
public sealed record InspectorDetailRow(string Label, string Value);

/// <summary>Holds frame labels, contact rows, status indicators, and protocol details for the inspector.</summary>
/// <param name="LogicalLabel">The one-based logical frame position and total, or a dash when unavailable.</param>
/// <param name="TimestampLabel">The formatted display timeline time, or source timestamp when no snapshot is available.</param>
/// <param name="Subtitle">The physical record and decoded format or register context displayed below the frame labels.</param>
/// <param name="ContactSummary">The active host contact counts by touch type.</param>
/// <param name="Contacts">The contact rows selected for the replay render mode.</param>
/// <param name="CrcLabel">The CRC validation result formatted for display.</param>
/// <param name="CrcFailed">Whether the decoded frame failed CRC validation.</param>
/// <param name="AsilLabel">The ASIL alarm state formatted for display.</param>
/// <param name="AsilRawLabel">The ASIL byte or TP error indicator formatted for display.</param>
/// <param name="AsilAlarm">Whether the decoded frame indicates an ASIL or TP error alarm.</param>
/// <param name="AllBreak">Whether the decoded frame has the all-break flag set.</param>
/// <param name="ProtocolRows">The format-specific protocol or register details displayed for the source record.</param>
public sealed record InspectorFramePresentation(
    string LogicalLabel,
    string TimestampLabel,
    string Subtitle,
    InspectorContactSummary ContactSummary,
    IReadOnlyList<InspectorContactRow> Contacts,
    string CrcLabel,
    bool CrcFailed,
    string AsilLabel,
    string AsilRawLabel,
    bool AsilAlarm,
    bool AllBreak,
    IReadOnlyList<InspectorDetailRow> ProtocolRows);

/// <summary>Builds inspector presentations and copyable text from source records and replay snapshots.</summary>
public static class InspectorPresentationBuilder
{
    /// <summary>Builds inspector labels, contact rows, and protocol details from a source record and optional decoded frame.</summary>
    public static InspectorFramePresentation Build(
        SourceRecord record,
        object? frame,
        ITouchReplaySnapshot? snapshot,
        int totalFrames,
        ReplayRenderMode renderMode,
        RegisterAnnotation? registerAnnotation = null)
    {
        var contacts = BuildContacts(snapshot, renderMode);
        var summary = BuildSummary(snapshot);
        var logicalLabel = snapshot is null || totalFrames <= 0
            ? "-"
            : $"{snapshot.LogicalIndex + 1} / {totalFrames}";
        var timestamp = snapshot is not null
            ? FormatClock(snapshot.Timeline.DisplayTime)
            : record.Timestamp?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-";

        return frame switch
        {
            CommonEventBufferFrame common => new InspectorFramePresentation(
                logicalLabel,
                timestamp,
                $"Physical #{record.Index} · Common {VersionText(common.Version)} · {(common.HostStateEligible ? "Host-state" : "Evidence only")}",
                summary,
                contacts,
                common.CrcValid ? "OK" : "FAIL",
                !common.CrcValid,
                common.TpAsilError || common.Asil.Alarm ? "ALARM" : "NORMAL",
                $"0x{common.Asil.Raw:X2}",
                common.TpAsilError || common.Asil.Alarm,
                common.AllBreak,
                FormatProtocolRows(common)),
            Desay97Frame desay => new InspectorFramePresentation(
                logicalLabel,
                timestamp,
                $"Physical #{record.Index} · Desay 0x97 / {ProfileText(desay.Profile)} · {(desay.HostStateEligible ? "Host-state" : "Evidence only")}",
                summary,
                contacts,
                desay.CrcValid ? "OK" : "FAIL",
                !desay.CrcValid,
                desay.TpAsilError ? "ALARM" : "NORMAL",
                desay.TpAsilError ? "TP" : "-",
                desay.TpAsilError,
                desay.AllBreak,
                FormatProtocolRows(desay)),
            _ => new InspectorFramePresentation(
                logicalLabel,
                timestamp,
                registerAnnotation?.Description is { } register
                    ? $"Physical #{record.Index} · {register.Readable} · {register.Profiles}"
                    : $"Physical #{record.Index} · Raw source record",
                summary,
                contacts,
                "-",
                false,
                "-",
                "-",
                false,
                false,
                FormatProtocolRows(record, registerAnnotation)),
        };
    }

    /// <summary>Builds up to ten contact rows ordered by ID for the selected replay render mode.</summary>
    public static IReadOnlyList<InspectorContactRow> BuildContacts(
        ITouchReplaySnapshot? snapshot,
        ReplayRenderMode renderMode)
    {
        if (snapshot is null) return [];

        var reported = snapshot.ReportedContacts
            .GroupBy(contact => contact.Id)
            .Select(group => group.Last())
            .ToDictionary(contact => contact.Id);
        IEnumerable<(ReplayContact Contact, bool Reported)> selected = renderMode switch
        {
            ReplayRenderMode.HostState => snapshot.HostContacts.Select(contact => (Contact: contact, Reported: false)),
            ReplayRenderMode.ReportedFrame => reported.Values.Select(contact => (Contact: contact, Reported: true)),
            _ => reported.Values.Select(contact => (Contact: contact, Reported: true)).Concat(
                snapshot.HostContacts
                    .Where(contact => !reported.ContainsKey(contact.Id))
                    .Select(contact => (Contact: contact, Reported: false))),
        };

        return selected
            .OrderBy(item => item.Contact.Id)
            .Take(10)
            .Select(item => new InspectorContactRow(
                item.Contact.Id,
                item.Contact.Type,
                item.Contact.Status,
                item.Contact.X,
                item.Contact.Y,
                item.Contact.Invalid,
                item.Reported))
            .ToArray();
    }

    /// <summary>Counts active host contacts by type and includes a global palm when no palm contact is active.</summary>
    public static InspectorContactSummary BuildSummary(ITouchReplaySnapshot? snapshot)
    {
        var active = snapshot?.HostContacts
            .Where(contact => contact.IsActive)
            .GroupBy(contact => contact.Id)
            .Select(group => group.Last())
            .ToArray() ?? [];
        var palms = active.Count(contact => contact.Type == TouchType.Palm);
        if (snapshot?.GlobalPalm == true && palms == 0) palms = 1;
        return new InspectorContactSummary(
            active.Count(contact => contact.Type == TouchType.Finger),
            active.Count(contact => contact.Type == TouchType.Glove),
            palms,
            active.Count(contact => contact.Type == TouchType.Reserved));
    }

    private static IReadOnlyList<InspectorDetailRow> FormatProtocolRows(CommonEventBufferFrame frame)
    {
        var rows = new List<InspectorDetailRow>
        {
            new("Format", $"Common {VersionText(frame.Version)}"),
            new("ASIL byte", $"0x{frame.Asil.Raw:X2}"),
            new("Button payload", frame.Button.Valid ? "Valid" : "Not present"),
        };
        if (frame.BusStatus is { } bus)
            rows.Add(new InspectorDetailRow("Bus counters", $"No response {bus.NoResponseCounter}, busy {bus.BusyCounter}"));
        if (frame.DiagnosticPacket is { } diagnostic)
        {
            rows.Add(new InspectorDetailRow("EMS bitmap", Convert.ToString(diagnostic.EmsBitmap, 2).PadLeft(4, '0')));
            rows.Add(new InspectorDetailRow("Global palm", diagnostic.PalmOn ? "On" : "Off"));
        }
        return rows;
    }

    private static IReadOnlyList<InspectorDetailRow> FormatProtocolRows(Desay97Frame frame) =>
    [
        new("Format", $"Desay 0x97, {ProfileText(frame.Profile)}"),
        new("Short / Open", $"{frame.Short} / {frame.Open}"),
        new("Phase 1", $"Physical #{frame.Packet.Probe.Index}, line {frame.Packet.Probe.Location.LineNumber}"),
        new("Phase 2", $"Physical #{frame.Packet.PayloadRead.Index}, line {frame.Packet.PayloadRead.Location.LineNumber}"),
    ];

    private static IReadOnlyList<InspectorDetailRow> FormatProtocolRows(
        SourceRecord record,
        RegisterAnnotation? registerAnnotation)
    {
        if (registerAnnotation?.Description is not { } register)
            return [new("Decode", "No decoded event is linked to this physical record")];
        return
        [
            new("Register", register.Readable),
            new("Raw value", register.RawValue),
            new("Profile", register.ProfileResolution),
        ];
    }

    /// <summary>Formats frame labels, contact counts, CRC and ASIL status, and contact rows for copying.</summary>
    public static string BuildCopyText(InspectorFramePresentation presentation)
    {
        var builder = new StringBuilder()
            .Append("Frame ").Append(presentation.LogicalLabel)
            .Append("  time ").AppendLine(presentation.TimestampLabel)
            .AppendLine(presentation.Subtitle)
            .Append("contacts ").Append(presentation.ContactSummary.Total)
            .Append("  finger ").Append(presentation.ContactSummary.Fingers)
            .Append("  glove ").Append(presentation.ContactSummary.Gloves)
            .Append("  palm ").AppendLine(presentation.ContactSummary.Palms.ToString(CultureInfo.InvariantCulture))
            .Append("CRC ").Append(presentation.CrcLabel)
            .Append("  ASIL ").Append(presentation.AsilLabel).Append(' ').AppendLine(presentation.AsilRawLabel);
        if (presentation.AllBreak) builder.AppendLine("ALL BREAK");
        foreach (var contact in presentation.Contacts)
            builder.AppendLine($"ID {contact.Id}  {contact.TypeLabel}  {contact.StateLabel}  X {contact.XLabel}  Y {contact.YLabel}  {contact.SourceLabel}");
        return builder.ToString().TrimEnd();
    }

    private static string FormatBytes(IReadOnlyList<byte> data) =>
        string.Join(' ', data.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));

    private static string FormatClock(TimeSpan value) =>
        value.TotalHours >= 1
            ? value.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
            : value.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static string VersionText(CommonEventBufferVersion version) => version switch
    {
        CommonEventBufferVersion.V82 => "0x82",
        CommonEventBufferVersion.V83 => "0x83",
        CommonEventBufferVersion.V84 => "0x84",
        CommonEventBufferVersion.V85 => "0x85",
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };

    private static string ProfileText(Desay97Profile profile) => profile switch
    {
        Desay97Profile.Standard => "Standard",
        Desay97Profile.BenzPalm => "Benz Palm",
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };
}
