// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Output preview values and transitions, owned by the UI thread.</summary>
internal sealed class OutputPreviewState
{
    internal ReplayOutputPreviewPlan? Plan { get; private set; }
    internal ITouchReplaySession? Replay { get; private set; }
    internal int FrameIndex { get; set; }
    internal bool Dirty { get; private set; }
    internal long Generation { get; private set; }
    internal bool PlanPending { get; private set; }
    internal string? PlanError { get; private set; }

    // MainWindow can defer pending/error updates until after its existing UI updates.
    internal long BeginRequest(bool? pending = null)
    {
        var generation = AdvanceGeneration();
        if (pending is { } requestedPending) SetRequestPending(requestedPending);
        return generation;
    }

    internal long AdvanceGeneration() => checked(++Generation);

    internal void SetRequestPending(bool pending)
    {
        PlanPending = pending;
        PlanError = null;
    }

    internal void CompleteRequest() => PlanPending = false;

    internal void FailRequest(string error) => PlanError = error;

    // Generation advances before cancellation; these values reset after service invalidation.
    internal void ResetRequest()
    {
        PlanPending = false;
        PlanError = null;
    }

    internal void InstallPlan(ReplayOutputPreviewPlan preview, ITouchReplaySession replay)
    {
        var framePlanChanged = !ReferenceEquals(Plan?.FramePlan, preview.FramePlan);
        Plan = preview;
        Replay = replay;
        FrameIndex = framePlanChanged
            ? 0
            : Math.Clamp(FrameIndex, 0, Math.Max(0, preview.Estimate.OutputFrameCount - 1));
    }

    internal void ClearPlan()
    {
        Plan = null;
        Replay = null;
        FrameIndex = 0;
        Dirty = false;
    }

    internal void MarkDirty() => Dirty = true;

    internal void MarkRendered() => Dirty = false;
}
