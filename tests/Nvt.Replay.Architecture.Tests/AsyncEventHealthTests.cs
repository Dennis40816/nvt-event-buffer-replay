// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Architecture.Tests;

public sealed class AsyncEventHealthTests
{
    [Fact]
    public async Task NonEventAsyncVoidMethodFailsRatchet()
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        workspace.UseFixture("StatePartWithAsyncWorker.cs.txt", "src/Nvt.Replay.Avalonia/MainWindow.Part1.cs");

        var result = await HealthFixture.CompareAsync(workspace.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("asyncVoid", result.Output, StringComparison.Ordinal);
        Assert.Contains("AsyncWorker.Execute()", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskReturningMethodRequiresLowerAsyncVoidAllowance()
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        workspace.UseFixture("StateOwnerTask.cs.txt", "src/Nvt.Replay.Avalonia/MainWindow.cs");

        var result = await HealthFixture.CompareAsync(workspace.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("asyncVoid: 1 -> 0", result.Output, StringComparison.Ordinal);
        Assert.Contains("Fixes must lower/delete baseline debt", result.Output, StringComparison.Ordinal);
    }
}
