namespace Nvt.Replay.Sources;

public sealed record NvtFwCommand(byte Code, string Name, bool Confirmed)
{
    public string Display => Confirmed ? Name : $"Unknown FW Command 0x{Code:X2}";
}

public static class NvtFwCommandParser
{
    public static NvtFwCommand? Parse(IReadOnlyList<byte> payload)
    {
        if (payload.Count == 0) return null;
        return payload[0] switch
        {
            0x23 => new NvtFwCommand(0x23, "Baseline Reset", Confirmed: true),
            var code => new NvtFwCommand(code, $"Unknown FW Command 0x{code:X2}", Confirmed: false),
        };
    }
}
