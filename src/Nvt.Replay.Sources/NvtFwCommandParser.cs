namespace Nvt.Replay.Sources;

public sealed record NvtFwCommand(byte Code, string Name, bool Confirmed)
{
    public string Display => Confirmed ? Name : $"Unknown FW Command 0x{Code:X2}";
}

public static class NvtFwCommandParser
{
    private static readonly IReadOnlyDictionary<byte, string> CommonCommands = new Dictionary<byte, string>
    {
        [0x11] = "Enter Deep Sleep Mode",
        [0x12] = "Enter Power Down Mode",
        [0x13] = "Enter Finger Detect Mode",
        [0x15] = "Enter Active Mode",
        [0x1A] = "Stop Green-mode Scan",
        [0x1B] = "Fix Scan Frequency",
        [0x1C] = "Fix 2D Baseline Update",
        [0x21] = "Real-time Raw Data Before Algorithm",
        [0x22] = "Real-time Raw Data After Algorithm",
        // User-confirmed product terminology takes precedence over the newer common-FW alias
        // "force calibration" for this command value.
        [0x23] = "Baseline Reset",
        [0x40] = "MP Test: None",
        [0x41] = "MP Test: CC Value",
        [0x42] = "MP Test: HWIP",
        [0x43] = "MP Test: Short",
        [0x44] = "MP Test: S1 Short to GND",
        [0x45] = "MP Test: Open",
        [0x46] = "MP Test: Pin",
        [0x47] = "MP Test: Noise Collection",
        [0x48] = "MP Test: Raw Data Check",
        [0x49] = "MP Test: INT",
        [0x4A] = "MP Test: TVSS Open",
        [0x4B] = "MP Test: Doze Noise",
        [0x4C] = "MP Test: Doze CC Value",
        [0xD1] = "Auto Engineering: Flow Hopping",
        [0xD2] = "Auto Engineering: Combined MP Test",
        [0xD3] = "Auto Engineering: DP ASIL Report",
        [0xD4] = "Auto Engineering: Stop FW Debug",
    };

    public static NvtFwCommand? Parse(IReadOnlyList<byte> payload)
    {
        if (payload.Count == 0) return null;
        var code = payload[0];
        return CommonCommands.TryGetValue(code, out var name)
            ? new NvtFwCommand(code, name, Confirmed: true)
            : new NvtFwCommand(code, $"Unknown FW Command 0x{code:X2}", Confirmed: false);
    }
}
