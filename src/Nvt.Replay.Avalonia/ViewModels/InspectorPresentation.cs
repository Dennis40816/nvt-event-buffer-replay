using System.Globalization;
using System.Text;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.ViewModels;

public sealed record InspectorContactRow(
    byte Id,
    TouchType Type,
    TouchStatus Status,
    ushort X,
    ushort Y,
    bool Invalid,
    bool ReportedThisFrame)
{
    public string IdLabel => $"ID {Id}";
    public string TypeLabel => Type switch
    {
        TouchType.Finger => "Finger",
        TouchType.Glove => "Glove",
        TouchType.Palm => "Palm",
        _ => "Other",
    };
    public string StateLabel => Invalid ? "INVALID" : Status switch
    {
        TouchStatus.Enter => "ENTER",
        TouchStatus.Move => "MOVE",
        TouchStatus.Break => "BREAK",
        _ when Type == TouchType.Palm => "PALM",
        _ => "IDLE",
    };
    public string XLabel => X.ToString(CultureInfo.InvariantCulture);
    public string YLabel => Y.ToString(CultureInfo.InvariantCulture);
    public string SourceLabel => ReportedThisFrame ? "REPORT" : "HOST";
    public string AccessibleLabel => $"{IdLabel}, {TypeLabel}, {StateLabel}, X {XLabel}, Y {YLabel}, {SourceLabel}";
}

public sealed record InspectorContactSummary(int Fingers, int Gloves, int Palms, int Other)
{
    public int Total => Fingers + Gloves + Palms + Other;
}

public sealed record InspectorDetailRow(string Label, string Value);

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

public static class InspectorPresentationBuilder
{
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
