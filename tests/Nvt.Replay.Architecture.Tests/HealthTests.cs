// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Text.Json.Nodes;

namespace Nvt.Replay.Architecture.Tests;

internal static class HealthFixture
{
    internal const string BaselinePath = "eng/code-health/baseline.json";
    internal const string HealthScript = "tools/repo-checks/repo-health.ps1";

    internal static JsonObject Enroll(JsonObject measured, JsonObject policy)
    {
        var entities = new JsonArray();
        foreach (var entity in measured["entities"]!.AsArray())
        {
            var ceilings = new JsonObject();
            foreach (var value in entity!["values"]!.AsObject())
                if (value.Value!.GetValue<int>() > policy["limits"]![value.Key]!.GetValue<int>())
                    ceilings[value.Key] = value.Value.DeepClone();
            if (ceilings.Count == 0) continue;
            var copy = entity.DeepClone().AsObject();
            copy.Remove("values");
            copy["ceilings"] = ceilings;
            copy["owner"] = "NFU";
            copy["issue"] = "health-refactor/2026-10-31";
            entities.Add(copy);
        }
        var baseline = new JsonObject();
        foreach (var key in new[] { "schemaVersion", "measurementVersion", "snapshotCommit", "limits", "findings" })
            baseline[key] = measured[key]!.DeepClone();
        baseline["entities"] = entities;
        foreach (var finding in baseline["findings"]!.AsArray())
        {
            finding!["owner"] = "NFU";
            finding["removeBy"] = "2026-10-31";
        }
        return baseline;
    }

    internal static async Task PrepareAsync(TempWorkspace workspace)
    {
        workspace.UseFixture("StateOwner.csproj.txt", "src/Nvt.Replay.Avalonia/Nvt.Replay.Avalonia.csproj");
        workspace.UseFixture("StateOwner.cs.txt", "src/Nvt.Replay.Avalonia/MainWindow.cs");
        for (var part = 1; part <= 8; part++)
            workspace.UseFixture("StatePart.cs.txt", $"src/Nvt.Replay.Avalonia/MainWindow.Part{part}.cs");
        File.Copy(Path.Combine(RepositoryFiles.Root, "global.json"), workspace.PathFor("global.json"));
        await ChildProcessFixture.CheckedAsync(workspace.Root, "git", "init", "-q");
        await CommitAsync(workspace.Root);
        var measurement = workspace.PathFor("measurement.json");
        var measured = await ChildProcessFixture.ScriptAsync(workspace.Root, HealthScript, "-Mode", "Measure", "-Repo", "nfu", "-Root", workspace.Root, "-OutputPath", measurement);
        if (measured.ExitCode != 0) throw new InvalidOperationException(measured.Output);
        var policy = RepositoryFiles.ReadJson(Path.Combine(RepositoryFiles.Root, BaselinePath));
        RepositoryFiles.WriteJson(workspace.PathFor(BaselinePath), Enroll(RepositoryFiles.ReadJson(measurement), policy));
        await CommitAsync(workspace.Root);
        var control = await VerifyAsync(workspace.Root);
        if (control.ExitCode != 0) throw new InvalidOperationException("Fixture control failed: " + control.Output);
    }

    private static async Task CommitAsync(string root)
    {
        await ChildProcessFixture.CheckedAsync(root, "git", "add", ".");
        await ChildProcessFixture.CheckedAsync(root, "git", "-c", "user.name=NFU Fixture", "-c", "user.email=fixture@example.invalid", "-c", "commit.gpgsign=false", "commit", "-qm", "Fixture baseline");
    }

    internal static Task<ChildResult> VerifyAsync(string root) => ChildProcessFixture.ScriptAsync(root, HealthScript, "-Mode", "Verify", "-Repo", "nfu", "-Root", root);
}

public sealed class HealthTests
{
    [Fact]
    public async Task CommittedBaselinePassesCoreVerify()
    {
        var baseline = RepositoryFiles.ReadJson(Path.Combine(RepositoryFiles.Root, HealthFixture.BaselinePath));
        Assert.Equal(1, baseline["schemaVersion"]!.GetValue<int>());
        var result = await HealthFixture.VerifyAsync(RepositoryFiles.Root);
        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Theory]
    [InlineData("stateMembers")]
    [InlineData("partialFiles")]
    [InlineData("asyncVoid")]
    public async Task LoweredAllowanceFailsCoreVerify(string metric)
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        var path = workspace.PathFor(HealthFixture.BaselinePath);
        var baseline = RepositoryFiles.ReadJson(path);
        LowerAllowance(baseline, metric);
        RepositoryFiles.WriteJson(path, baseline);
        var result = await HealthFixture.VerifyAsync(workspace.Root);
        Assert.True(result.ExitCode == 1 && result.Output.Contains(metric, StringComparison.Ordinal), result.Output);
    }

    [Fact]
    public async Task InvalidBaselineIsRejectedAsInputError()
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        File.WriteAllText(workspace.PathFor(HealthFixture.BaselinePath), "{");
        var result = await HealthFixture.VerifyAsync(workspace.Root);
        Assert.True(result.ExitCode == 2 && result.Output.Contains("invalid JSON", StringComparison.Ordinal), result.Output);
    }

    private static void LowerAllowance(JsonObject baseline, string metric)
    {
        if (metric == "asyncVoid") baseline["findings"]!.AsArray().Clear();
        else baseline["entities"]!.AsArray().Single(entity => entity!["kind"]!.GetValue<string>() == "type")!["ceilings"]!.AsObject().Remove(metric);
    }
}
