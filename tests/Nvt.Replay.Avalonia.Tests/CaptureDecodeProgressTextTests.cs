// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class CaptureDecodeProgressTextTests
{
    [Theory]
    [InlineData(CaptureDecodePhase.SelectingFormat, "Selecting Event Buffer format")]
    [InlineData(CaptureDecodePhase.ProjectingRegisters, "Applying IC register profile")]
    [InlineData(CaptureDecodePhase.DecodingFrames, "Decoding Event Buffer frames")]
    [InlineData(CaptureDecodePhase.BuildingWorkspace, "Building replay workspace")]
    [InlineData(CaptureDecodePhase.Ready, "Replay workspace ready")]
    public void PhaseUsesOperatorWording(CaptureDecodePhase phase, string expected)
    {
        Assert.Equal(expected, CaptureDecodeProgressText.Phase(phase));
    }

    [Theory]
    [InlineData(CaptureDecodePhase.ProbingSource)]
    [InlineData(CaptureDecodePhase.HashingSource)]
    [InlineData(CaptureDecodePhase.IndexingRecords)]
    [InlineData(CaptureDecodePhase.SourceReady)]
    public void PhaseFallsBackToEnumName(CaptureDecodePhase phase)
    {
        Assert.Equal(phase.ToString(), CaptureDecodeProgressText.Phase(phase));
    }
}
