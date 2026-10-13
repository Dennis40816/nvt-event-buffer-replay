// Copyright (c) 2026 Dennis Liu. All rights reserved.

using System.Collections;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Finds a register profile choice by IC family in the list the profile combo boxes hold.</summary>
internal static class RegisterProfileChoices
{
    /// <summary>
    /// Matches the IC family case-insensitively. A null family matches the "Unconfirmed" choice.
    /// Returns null when the source is not a choice list or no choice matches.
    /// </summary>
    internal static RegisterProfileChoice? Find(IEnumerable? itemsSource, string? icFamily) =>
        (itemsSource as IEnumerable<RegisterProfileChoice>)?
            .FirstOrDefault(item => string.Equals(item.IcFamily, icFamily, StringComparison.OrdinalIgnoreCase));
}
