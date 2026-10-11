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

        var result = await HealthFixture.VerifyAsync(workspace.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("asyncVoid", result.Output, StringComparison.Ordinal);
        Assert.Contains("AsyncWorker.Execute()", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TaskReturningMethodLowersAsyncVoidBaseline()
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        workspace.UseFixture("StateOwnerTask.cs.txt", "src/Nvt.Replay.Avalonia/MainWindow.cs");

        var result = await ChildProcessFixture.ScriptAsync(workspace.Root, HealthFixture.HealthScript,
            "-Mode", "LowerBaseline", "-Repo", "nfu", "-Root", workspace.Root);

        Assert.Equal(0, result.ExitCode);
        var baseline = RepositoryFiles.ReadJson(workspace.PathFor(HealthFixture.BaselinePath));
        Assert.DoesNotContain(baseline["findings"]!.AsArray(), finding => finding!["rule"]!.GetValue<string>() == "asyncVoid");
    }
}
