using Nvt.Replay.Core;

namespace Nvt.Replay.Analysis;

public sealed class NvtRegisterActivityMonitor
{
    private readonly Dictionary<uint, byte[]> previousByAddress = [];

    public IReadOnlyList<ReplayDiagnostic> Observe(IEnumerable<SourceRecord> records)
    {
        var diagnostics = new List<ReplayDiagnostic>();
        foreach (var record in records.Where(item => item.Operation == BusOperation.Read && item.Address is not null))
        {
            var address = record.Address!.Value;
            if (address is not (0x99060 or 0x99070 or 0x99076 or 0x99078)) continue;
            var raw = record.Data.ToArray();
            if (raw.Length == 0) continue;
            var changed = !previousByAddress.TryGetValue(address, out var previous) || !raw.SequenceEqual(previous);
            previousByAddress[address] = raw;
            diagnostics.Add(new ReplayDiagnostic(
                DiagnosticSeverity.Info,
                Code(address),
                Message(address, raw),
                record.StableId,
                record.Location,
                new Dictionary<string, string>
                {
                    ["register_address"] = $"0x{address:X}",
                    ["raw"] = string.Join(' ', raw.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))),
                    ["changed_from_previous_sample"] = changed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["meaning"] = Meaning(address, raw),
                }));
        }
        return diagnostics;
    }

    private static string Code(uint address) => address switch
    {
        0x99060 => "NVT_FW_STATE_SAMPLE",
        0x99070 => "NVT_FRAME_COUNTER_SAMPLE",
        0x99076 => "NVT_DP_VERSION_SAMPLE",
        0x99078 => "NVT_TP_FW_VERSION_SAMPLE",
        _ => throw new ArgumentOutOfRangeException(nameof(address)),
    };

    private static string Message(uint address, IReadOnlyList<byte> raw) => address switch
    {
        0x99060 => $"FW State read: 0x{raw[0]:X2}{(raw[0] == 0xA3 ? " (Normal Run)" : string.Empty)}.",
        0x99070 => $"Frame counter sample read: {string.Join(' ', raw.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))}.",
        0x99076 => $"DP Version read: {string.Join(' ', raw.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))}.",
        0x99078 => $"TP FW Version read: {string.Join(' ', raw.Select(value => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)))}.",
        _ => throw new ArgumentOutOfRangeException(nameof(address)),
    };

    private static string Meaning(uint address, IReadOnlyList<byte> raw) =>
        address == 0x99060 && raw[0] == 0xA3 ? "normal_run" : "raw_only";
}
