// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Decides whether applying an IC register profile may start a decode right away.</summary>
internal static class RegisterProfileDecodeGate
{
    /// <summary>Position of the "0x97" item in the Event Buffer Version combo box (MainWindow.axaml).</summary>
    internal const int Desay97VersionIndex = 4;

    /// <summary>
    /// A version must be selected. Desay 0x97 also needs a confirmed IC family and a Palm profile.
    /// </summary>
    internal static bool CanDecode(int eventVersionIndex, string? icFamily, int desay97ProfileIndex) =>
        eventVersionIndex >= 0 &&
        (eventVersionIndex != Desay97VersionIndex || (icFamily is not null && desay97ProfileIndex >= 0));
}
