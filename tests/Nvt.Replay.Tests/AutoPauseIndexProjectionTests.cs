// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Tests;

public sealed class AutoPauseIndexProjectionTests
{
    [Fact]
    public async Task AutoPauseIndexIsReachableFromTheAdoptedDecodeAndGoesWithClear()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var projection = await CaptureTestSupport.DecodeAsync(workspace);

        Assert.Same(projection, workspace.Replay);
        Assert.NotNull(workspace.Replay?.Decoded.Workspace.AutoPauseIndex);

        workspace.Clear();

        Assert.Null(workspace.Replay);
    }
}
