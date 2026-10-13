// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>The status and hint texts shown while an IC register profile is applied or removed.</summary>
internal static class RegisterProfileText
{
    internal static string Busy(string? icFamily) =>
        icFamily is null ? "Removing IC register profile" : $"Applying IC profile {icFamily}";

    internal static string AppliedHint(string? icFamily, int ambiguousEvents) =>
        icFamily is null
            ? $"IC profile unconfirmed · {ambiguousEvents:N0} collision-prone register events remain raw-only"
            : $"IC profile {icFamily} confirmed · register semantics and Event Buffer base reapplied";

    internal static string AppliedStatus(string? icFamily) =>
        icFamily is null
            ? "Register profile removed; original capture remains unchanged"
            : $"Register profile {icFamily} applied without changing source bytes";

    internal static string NoAddressEvidenceHint(int address) =>
        $"I²C 0x{address:X2} selected · no complete Event Buffer address evidence; IC profile remains unconfirmed.";
}
