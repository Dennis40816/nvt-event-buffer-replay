using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Analysis;

public sealed class NvtRegisterActivityMonitor
{
    private readonly Dictionary<string, byte[]> previousByRegister = [];

    public IReadOnlyList<ReplayDiagnostic> Observe(IEnumerable<SourceRecord> records)
    {
        var diagnostics = new List<ReplayDiagnostic>();
        foreach (var source in records.Where(item => item.Operation == BusOperation.Read && item.Address is not null))
        {
            var record = source.SourceFields?.ContainsKey("register_readable") == true
                ? source
                : NvtRegisterCatalog.Annotate(source);
            var name = record.SourceFields?.GetValueOrDefault("register_name");
            if (name is not ("fw_state" or "frame_counter" or "dp_version" or "tp_fw_version")) continue;
            var address = record.Address!.Value;
            var raw = record.Data.ToArray();
            if (raw.Length == 0) continue;
            var key = $"{record.SourceFields?.GetValueOrDefault("register_profile") ?? "unknown"}:{address:X}";
            var changed = !previousByRegister.TryGetValue(key, out var previous) || !raw.SequenceEqual(previous);
            previousByRegister[key] = raw;
            diagnostics.Add(new ReplayDiagnostic(
                DiagnosticSeverity.Info,
                Code(name),
                Message(name, raw),
                record.StableId,
                record.Location,
                new Dictionary<string, string>
                {
                    ["register_address"] = $"0x{address:X}",
                    ["raw"] = string.Join(' ', raw.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))),
                    ["changed_from_previous_sample"] = changed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["meaning"] = Meaning(name, raw),
                    ["register_profile"] = record.SourceFields?.GetValueOrDefault("register_profile") ?? "unknown",
                }));
        }
        return diagnostics;
    }

    private static string Code(string name) => name switch
    {
        "fw_state" => "NVT_FW_STATE_SAMPLE",
        "frame_counter" => "NVT_FRAME_COUNTER_SAMPLE",
        "dp_version" => "NVT_DP_VERSION_SAMPLE",
        "tp_fw_version" => "NVT_TP_FW_VERSION_SAMPLE",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string Message(string name, IReadOnlyList<byte> raw) => name switch
    {
        "fw_state" => $"FW State read: 0x{raw[0]:X2}{(raw[0] == 0xA3 ? " (Normal Run)" : string.Empty)}.",
        "frame_counter" => $"Frame counter sample read: {Raw(raw)}.",
        "dp_version" => $"DP Version read: {Raw(raw)}.",
        "tp_fw_version" => $"TP FW Version read: {Raw(raw)}.",
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static string Meaning(string name, IReadOnlyList<byte> raw) =>
        name == "fw_state" && raw[0] == 0xA3 ? "normal_run" : "raw_only";

    private static string Raw(IReadOnlyList<byte> raw) =>
        string.Join(' ', raw.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
