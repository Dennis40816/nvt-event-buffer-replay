// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests.ViewModels;

public sealed class OutputPreviewStateTests
{
    [Fact]
    public void DefaultStateHasNoPlanOrRequest()
    {
        var state = new OutputPreviewState();

        Assert.Null(state.Plan);
        Assert.Null(state.Replay);
        Assert.Equal(0, state.FrameIndex);
        Assert.False(state.Dirty);
        Assert.Equal(0, state.Generation);
        Assert.False(state.PlanPending);
        Assert.Null(state.PlanError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BeginRequestBumpsGenerationSetsPendingAndClearsError(bool pending)
    {
        var state = new OutputPreviewState();
        state.SetRequestPending(!pending);
        state.FailRequest("Previous planning error");

        var generation = state.BeginRequest(pending);

        Assert.Equal(1, generation);
        Assert.Equal(generation, state.Generation);
        Assert.Equal(pending, state.PlanPending);
        Assert.Null(state.PlanError);
    }

    [Fact]
    public void BeginRequestWithoutPendingDefersPendingAndErrorUpdates()
    {
        var state = new OutputPreviewState();
        state.SetRequestPending(true);
        state.FailRequest("Previous planning error");

        var generation = state.BeginRequest();

        Assert.Equal(1, generation);
        Assert.True(state.PlanPending);
        Assert.Equal("Previous planning error", state.PlanError);
    }

    [Fact]
    public void NewRequestMakesPreviousGenerationStale()
    {
        var state = new OutputPreviewState();
        var previousGeneration = state.BeginRequest(true);

        var currentGeneration = state.BeginRequest(false);

        Assert.NotEqual(previousGeneration, state.Generation);
        Assert.Equal(previousGeneration + 1, currentGeneration);
        Assert.Equal(currentGeneration, state.Generation);
    }

    [Fact]
    public void SetRequestPendingClearsErrorWithoutAdvancingGeneration()
    {
        var state = new OutputPreviewState();
        var generation = state.BeginRequest();
        state.FailRequest("Previous planning error");

        state.SetRequestPending(true);

        Assert.Equal(generation, state.Generation);
        Assert.True(state.PlanPending);
        Assert.Null(state.PlanError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailRequestKeepsPendingIndependent(bool pending)
    {
        var state = new OutputPreviewState();
        state.BeginRequest(pending);

        state.FailRequest("Planning error");

        Assert.Equal(pending, state.PlanPending);
        Assert.Equal("Planning error", state.PlanError);
    }

    [Fact]
    public void CompleteRequestClearsPendingAndKeepsError()
    {
        var state = new OutputPreviewState();
        state.BeginRequest(true);
        state.FailRequest("Planning error");

        state.CompleteRequest();

        Assert.False(state.PlanPending);
        Assert.Equal("Planning error", state.PlanError);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(2, 2)]
    [InlineData(99, 3)]
    public void InstallSameFramePlanKeepsFrameIndexWithinBounds(int previousIndex, int expectedIndex)
    {
        var replay = CreateReplay();
        var preview = CreatePlan(replay);
        var state = new OutputPreviewState();
        state.InstallPlan(preview, replay);
        state.FrameIndex = previousIndex;
        var replacementReplay = CreateReplay();
        var replacement = preview with { Identity = preview.Identity with { ReplayIdentity = "replacement" } };

        state.InstallPlan(replacement, replacementReplay);

        Assert.Same(replacement, state.Plan);
        Assert.Same(replacementReplay, state.Replay);
        Assert.Equal(expectedIndex, state.FrameIndex);
    }

    [Fact]
    public void InstallChangedFramePlanResetsFrameIndex()
    {
        var replay = CreateReplay();
        var state = new OutputPreviewState();
        var preview = CreatePlan(replay);
        state.InstallPlan(preview, replay);
        state.FrameIndex = 2;
        var replacement = CreatePlan(replay);
        Assert.NotSame(preview.FramePlan, replacement.FramePlan);

        state.InstallPlan(replacement, replay);

        Assert.Same(replacement, state.Plan);
        Assert.Equal(0, state.FrameIndex);
    }

    [Fact]
    public void InstallSameFramePlanWithEmptyEstimateClampsFrameIndexToZero()
    {
        var replay = CreateReplay();
        var preview = CreatePlan(replay);
        var state = new OutputPreviewState();
        state.InstallPlan(preview, replay);
        state.FrameIndex = 2;
        var replacement = preview with { Estimate = preview.Estimate with { OutputFrameCount = 0 } };

        state.InstallPlan(replacement, replay);

        Assert.Equal(0, state.FrameIndex);
    }

    [Fact]
    public void InstallPlanLeavesRequestAndDirtyValuesUnchanged()
    {
        var replay = CreateReplay();
        var state = new OutputPreviewState();
        var generation = state.BeginRequest(true);
        state.FailRequest("Planning error");
        state.MarkDirty();

        state.InstallPlan(CreatePlan(replay), replay);

        Assert.Equal(generation, state.Generation);
        Assert.True(state.PlanPending);
        Assert.Equal("Planning error", state.PlanError);
        Assert.True(state.Dirty);
    }

    [Fact]
    public void ClearPlanResetsPreviewAndKeepsRequestValues()
    {
        var replay = CreateReplay();
        var state = new OutputPreviewState();
        state.InstallPlan(CreatePlan(replay), replay);
        state.FrameIndex = 2;
        state.MarkDirty();
        var generation = state.BeginRequest(true);
        state.FailRequest("Planning error");

        state.ClearPlan();

        Assert.Null(state.Plan);
        Assert.Null(state.Replay);
        Assert.Equal(0, state.FrameIndex);
        Assert.False(state.Dirty);
        Assert.Equal(generation, state.Generation);
        Assert.True(state.PlanPending);
        Assert.Equal("Planning error", state.PlanError);
    }

    [Fact]
    public void ErrorSurvivesCompletionAndClearUntilNextRequest()
    {
        var state = new OutputPreviewState();
        state.BeginRequest(true);
        state.FailRequest("Planning error");
        state.CompleteRequest();
        state.ClearPlan();
        Assert.Equal("Planning error", state.PlanError);

        state.BeginRequest(false);

        Assert.Null(state.PlanError);
    }

    [Fact]
    public void InvalidationAdvancesGenerationBeforeRequestValuesReset()
    {
        var state = new OutputPreviewState();
        var generation = state.BeginRequest(true);
        state.FailRequest("Planning error");

        var invalidatedGeneration = state.AdvanceGeneration();

        Assert.Equal(generation + 1, invalidatedGeneration);
        Assert.NotEqual(generation, state.Generation);
        Assert.True(state.PlanPending);
        Assert.Equal("Planning error", state.PlanError);

        state.ResetRequest();

        Assert.Equal(invalidatedGeneration, state.Generation);
        Assert.False(state.PlanPending);
        Assert.Null(state.PlanError);
    }

    [Fact]
    public void RenderingClearsDirtyWithoutClearingPlan()
    {
        var replay = CreateReplay();
        var preview = CreatePlan(replay);
        var state = new OutputPreviewState();
        state.InstallPlan(preview, replay);
        state.MarkDirty();
        Assert.True(state.Dirty);

        state.MarkRendered();

        Assert.False(state.Dirty);
        Assert.Same(preview, state.Plan);
    }

    private static CommonReplaySession CreateReplay()
    {
        var source = new SourceRecord(0, "synthetic:0", null, BusOperation.Read, "touch", null,
            CommonEventBufferDecoder.FrameLength, new byte[CommonEventBufferDecoder.FrameLength],
            string.Empty, new SourceLocation(0, 1));
        var frame = Assert.IsType<CommonEventBufferFrame>(
            new CommonEventBufferDecoder().Decode(source, CommonEventBufferVersion.V83).Frame);
        return new CommonReplaySession([frame]);
    }

    private static ReplayOutputPreviewPlan CreatePlan(CommonReplaySession replay)
    {
        var settings = ReplayOutputSettings.Default;
        var options = new ReplayExportOptions("synthetic.mp4", settings.Range,
            FrameRate: settings.FrameRate, Clock: settings.Clock);
        var framePlan = ReplayFramePlan.Freeze(replay, options, [new ReplayFramePlanEntry(0, 4)]);
        return new ReplayOutputPreviewPlan(
            new ReplayOutputPlanIdentity("synthetic", replay.Count, replay.FrameInterval, settings),
            framePlan,
            new ReplayOutputEstimate(4, TimeSpan.FromSeconds(4d / settings.FrameRate), 0, 0, false));
    }
}
