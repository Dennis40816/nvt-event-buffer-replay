// Copyright (c) 2026 Dennis Liu. All rights reserved.
namespace Nvt.Replay.Architecture.Tests;

public sealed class DuplicationTests
{
    private const string Script = "scripts/measure-test-duplication.ps1";

    [Theory]
    [InlineData("metrics", "tempPaths")]
    [InlineData("metrics", "workspaceClasses")]
    [InlineData("metrics", "fakeClocks")]
    [InlineData("metrics", "realTimeWaits")]
    [InlineData("metrics", "elapsedTime")]
    [InlineData("metrics", "headlessSetup")]
    [InlineData("metrics", "childProcesses")]
    [InlineData("metrics", "repoRootFinders")]
    [InlineData("metrics", "sourceTextReads")]
    [InlineData("duplicateFakeClasses", "FakeReplay")]
    public async Task RaisedDuplicationFails(string group, string metric)
    {
        using var workspace = new TempWorkspace();
        workspace.UseFixture("Duplication.cs.txt", "tests/First.cs");
        workspace.UseFixture("DuplicateFake.cs.txt", "tests/Second.cs");
        var control = await ChildProcessFixture.ScriptAsync(workspace.Root, Script, "-Root", workspace.Root);
        Assert.True(control.ExitCode == 0, control.Output);
        var path = workspace.PathFor("eng/code-health/test-duplication.json");
        var baseline = RepositoryFiles.ReadJson(path);
        baseline[group]![metric]!["count"] = 0;
        RepositoryFiles.WriteJson(path, baseline);
        var result = await ChildProcessFixture.ScriptAsync(workspace.Root, Script, "-Root", workspace.Root, "-Verify");
        Assert.True(result.ExitCode == 1 && result.Output.Contains(metric + " increased", StringComparison.Ordinal), result.Output);
    }
}
