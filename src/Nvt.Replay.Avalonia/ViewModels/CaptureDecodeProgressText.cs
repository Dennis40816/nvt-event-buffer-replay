// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Status-line wording for each decode phase.</summary>
internal static class CaptureDecodeProgressText
{
    internal static string Phase(CaptureDecodePhase phase) => phase switch
    {
        CaptureDecodePhase.SelectingFormat => "Selecting Event Buffer format",
        CaptureDecodePhase.ProjectingRegisters => "Applying IC register profile",
        CaptureDecodePhase.DecodingFrames => "Decoding Event Buffer frames",
        CaptureDecodePhase.BuildingWorkspace => "Building replay workspace",
        CaptureDecodePhase.Ready => "Replay workspace ready",
        _ => phase.ToString(),
    };
}
