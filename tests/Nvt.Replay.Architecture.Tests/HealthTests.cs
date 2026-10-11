// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Text.Json.Nodes;

namespace Nvt.Replay.Architecture.Tests;

internal static class HealthFixture
{
    internal const string BaselinePath = "eng/code-health/baseline.json";
    internal const string TestDebtPath = "eng/code-health/test-debt.json";
    internal const string HealthScript = "tools/repo-checks/repo-health.ps1";
    private const string ComparisonScript = "tests/Nvt.Replay.Architecture.Tests/Fixtures/CompareHealth.ps1";

    private static JsonObject CreateBaseline(JsonObject measured)
    {
        var entities = new JsonArray();
        foreach (var entity in measured["entities"]!.AsArray())
        {
            var ceilings = new JsonObject();
            foreach (var value in entity!["values"]!.AsObject())
                if (value.Value!.GetValue<int>() > measured["limits"]![value.Key]!.GetValue<int>())
                    ceilings[value.Key] = value.Value.DeepClone();
            if (ceilings.Count == 0) continue;
            var copy = CopyKeys(entity.AsObject(), "project", "kind", "symbol", "locations", "contentHash");
            copy["ceilings"] = ceilings;
            copy["owner"] = "NFU fixture";
            copy["issue"] = "synthetic-fixture";
            entities.Add(copy);
        }
        var baseline = CopyKeys(measured, "schemaVersion", "measurementVersion", "limits");
        baseline["snapshotCommit"] = new string('0', 40);
        baseline["entities"] = entities;
        baseline["findings"] = CreateFindings(measured, testDebt: false);
        return baseline;
    }

    private static JsonObject CopyKeys(JsonObject source, params string[] keys)
    {
        var copy = new JsonObject();
        foreach (var key in keys) copy[key] = source[key]!.DeepClone();
        return copy;
    }

    private static bool IsTestRule(string rule) => rule is "timeWaitsInTests" or "elapsedAssertionsInTests"
        or "tempPathInTests" or "sourceTextReadsInTests" or "headlessSessionSetups"
        or "childProcessInTests" or "sameNameFakes";

    private static JsonArray CreateFindings(JsonObject measured, bool testDebt)
    {
        var findings = new JsonArray();
        foreach (var finding in measured["findings"]!.AsArray().Where(finding => IsTestRule(finding!["rule"]!.GetValue<string>()) == testDebt))
        {
            var copy = CopyKeys(finding!.AsObject(), "rule", "project", "path", "member", "symbol", "syntaxHash", "count");
            copy["owner"] = "NFU fixture";
            findings.Add(copy);
        }
        return findings;
    }

    internal static async Task PrepareAsync(TempWorkspace workspace)
    {
        workspace.UseFixture("StateOwner.csproj.txt", "src/Nvt.Replay.Avalonia/Nvt.Replay.Avalonia.csproj");
        workspace.UseFixture("StateOwner.cs.txt", "src/Nvt.Replay.Avalonia/MainWindow.cs");
        for (var part = 1; part <= 8; part++)
            workspace.UseFixture("StatePart.cs.txt", $"src/Nvt.Replay.Avalonia/MainWindow.Part{part}.cs");
        workspace.UseFixture("StateOwner.csproj.txt", "tests/HealthProbe.Tests/HealthProbe.Tests.csproj");
        workspace.UseFixture("Duplication.cs.txt", "tests/HealthProbe.Tests/First.cs");
        workspace.UseFixture("DuplicateFake.cs.txt", "tests/HealthProbe.Tests/Second.cs");
        File.Copy(Path.Combine(RepositoryFiles.Root, "global.json"), workspace.PathFor("global.json"));
        var measurement = workspace.PathFor("measurement.json");
        var measured = await ChildProcessFixture.ScriptAsync(workspace.Root, HealthScript, "-Mode", "Measure", "-Repo", "nfu", "-Root", workspace.Root, "-OutputPath", measurement);
        if (measured.ExitCode != 0) throw new InvalidOperationException(measured.Output);
        var snapshot = RepositoryFiles.ReadJson(measurement);
        RepositoryFiles.WriteJson(workspace.PathFor(BaselinePath), CreateBaseline(snapshot));
        var debt = CopyKeys(snapshot, "schemaVersion", "measurementVersion");
        debt["snapshotCommit"] = new string('0', 40);
        debt["findings"] = CreateFindings(snapshot, testDebt: true);
        RepositoryFiles.WriteJson(workspace.PathFor(TestDebtPath), debt);
        var control = await CompareMeasurementAsync(workspace.Root);
        if (control.ExitCode != 0) throw new InvalidOperationException("Fixture control failed: " + control.Output);
    }

    internal static async Task<ChildResult> CompareAsync(string root)
    {
        var measured = await ChildProcessFixture.ScriptAsync(root, HealthScript, "-Mode", "Measure", "-Repo", "nfu", "-Root", root, "-OutputPath", Path.Combine(root, "measurement.json"));
        return measured.ExitCode == 0 ? await CompareMeasurementAsync(root) : measured;
    }

    private static Task<ChildResult> CompareMeasurementAsync(string root) =>
        ChildProcessFixture.ScriptAsync(root, ComparisonScript, "-Root", root, "-HealthScript", Path.Combine(RepositoryFiles.Root, HealthScript));

    internal static void RemoveRuleAllowance(JsonObject ledger, string rule)
    {
        var findings = ledger["findings"]!.AsArray();
        foreach (var finding in findings.Where(finding => finding!["rule"]!.GetValue<string>() == rule).ToArray())
            findings.Remove(finding);
    }
}

public sealed class HealthTests
{
    [Theory]
    [InlineData("stateMembers")]
    [InlineData("partialFiles")]
    [InlineData("asyncVoid")]
    public async Task LoweredAllowanceFailsVerifyComparison(string metric)
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        var path = workspace.PathFor(HealthFixture.BaselinePath);
        var baseline = RepositoryFiles.ReadJson(path);
        LowerAllowance(baseline, metric);
        RepositoryFiles.WriteJson(path, baseline);

        var result = await HealthFixture.CompareAsync(workspace.Root);

        Assert.True(result.ExitCode == 1 && result.Output.Contains(metric, StringComparison.Ordinal), result.Output);
    }

    [Fact]
    public async Task InvalidBaselineIsRejectedAsInputError()
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        File.WriteAllText(workspace.PathFor(HealthFixture.BaselinePath), "{");

        var result = await HealthFixture.CompareAsync(workspace.Root);

        Assert.True(result.ExitCode == 2 && result.Output.Contains("invalid JSON", StringComparison.Ordinal), result.Output);
    }

    private static void LowerAllowance(JsonObject baseline, string metric)
    {
        if (metric == "asyncVoid") HealthFixture.RemoveRuleAllowance(baseline, metric);
        else baseline["entities"]!.AsArray().First(entity => entity!["symbol"]!.GetValue<string>() == "Nvt.Replay.Avalonia.MainWindow")!["ceilings"]!.AsObject().Remove(metric);
    }

    [Theory]
    [InlineData(HealthFixture.BaselinePath, "workspaceDuplicates")]
    [InlineData(HealthFixture.BaselinePath, "fakeClockDuplicates")]
    [InlineData(HealthFixture.TestDebtPath, "tempPathInTests")]
    [InlineData(HealthFixture.TestDebtPath, "timeWaitsInTests")]
    [InlineData(HealthFixture.TestDebtPath, "elapsedAssertionsInTests")]
    [InlineData(HealthFixture.TestDebtPath, "headlessSessionSetups")]
    [InlineData(HealthFixture.TestDebtPath, "childProcessInTests")]
    [InlineData(HealthFixture.TestDebtPath, "sourceTextReadsInTests")]
    [InlineData(HealthFixture.TestDebtPath, "sameNameFakes")]
    public async Task RemovedRuleAllowanceFailsVerifyComparison(string ledgerPath, string rule)
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        var path = workspace.PathFor(ledgerPath);
        var ledger = RepositoryFiles.ReadJson(path);
        Assert.Contains(ledger["findings"]!.AsArray(), finding => finding!["rule"]!.GetValue<string>() == rule);
        HealthFixture.RemoveRuleAllowance(ledger, rule);
        RepositoryFiles.WriteJson(path, ledger);

        var result = await HealthFixture.CompareAsync(workspace.Root);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(rule, result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NewFakeFingerprintFailsWithUnchangedTotalCount()
    {
        using var workspace = new TempWorkspace();
        await HealthFixture.PrepareAsync(workspace);
        var oldCount = FakeCount(RepositoryFiles.ReadJson(workspace.PathFor("measurement.json")));
        workspace.UseFixture("RenamedFake.cs.txt", "tests/HealthProbe.Tests/Second.cs");

        var result = await HealthFixture.CompareAsync(workspace.Root);

        Assert.Equal(oldCount, FakeCount(RepositoryFiles.ReadJson(workspace.PathFor("measurement.json"))));
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("sameNameFakes: 0 -> 1", result.Output, StringComparison.Ordinal);
    }

    private static int FakeCount(JsonObject measurement) => measurement["findings"]!.AsArray()
        .Where(finding => finding!["rule"]!.GetValue<string>() == "sameNameFakes")
        .Sum(finding => finding!["count"]!.GetValue<int>());
}
