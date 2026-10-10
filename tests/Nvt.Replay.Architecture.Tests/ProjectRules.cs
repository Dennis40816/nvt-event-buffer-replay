// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Xml.Linq;
using System.Collections.Frozen;

namespace Nvt.Replay.Architecture.Tests;

internal static class ProjectRules
{
    // Freeze today's inward edges. Removal is allowed; a new edge needs review.
    private static readonly FrozenDictionary<string, FrozenSet<string>> _allowed = new Dictionary<string, string[]>
    {
        ["Core"] = [], ["Sources"] = ["Core"], ["Formats"] = ["Core"],
        ["Analysis"] = ["Core", "Formats", "Sources"],
        ["Rendering"] = ["Core", "Formats", "Analysis"],
        ["Cli"] = ["Core", "Sources", "Formats", "Analysis", "Rendering"],
        ["Avalonia"] = ["Core", "Sources", "Formats", "Analysis", "Rendering"]
    }.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet(StringComparer.Ordinal), StringComparer.Ordinal);

    internal static string[] Check(string root, string rule)
    {
        var projects = Directory.EnumerateFiles(Path.Combine(root, "src"), "Nvt.Replay.*.csproj", SearchOption.AllDirectories).ToArray();
        return projects.Length == 0 ? ["No production projects discovered."] : projects.SelectMany(path => CheckProject(path, rule)).ToArray();
    }

    private static IEnumerable<string> CheckProject(string path, string rule)
    {
        var name = Path.GetFileNameWithoutExtension(path)["Nvt.Replay.".Length..];
        var project = XDocument.Load(path);
        foreach (var reference in project.Descendants().Where(node =>
                     node.Name.LocalName is "ProjectReference" or "PackageReference"))
        {
            var include = reference.Attribute("Include")?.Value ?? reference.Attribute("Update")?.Value ?? "";
            var target = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
            if (rule == "direction" && reference.Name.LocalName == "ProjectReference" &&
                target.StartsWith("Nvt.Replay.", StringComparison.Ordinal) &&
                (!_allowed.TryGetValue(name, out var allowed) || !allowed.Contains(target["Nvt.Replay.".Length..])))
                yield return $"{name} -> {target}";
            if (rule == "tests" && reference.Name.LocalName == "ProjectReference" &&
                (include.Replace('\\', '/').Split('/').Contains("tests", StringComparer.OrdinalIgnoreCase) ||
                 target.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)))
                yield return $"{name} references tests: {include}";
            if (rule == "avalonia" && name != "Avalonia" &&
                (include.Contains("Avalonia", StringComparison.OrdinalIgnoreCase)))
                yield return $"{name} references Avalonia: {include}";
        }
    }
}

public sealed class ProjectRulesTests
{
    [Theory]
    [InlineData("direction")]
    [InlineData("tests")]
    [InlineData("avalonia")]
    public void ProductionProjectsRespectTheirLayers(string rule) => Assert.Empty(ProjectRules.Check(RepositoryFiles.Root, rule));

    [Theory]
    [InlineData("direction")]
    [InlineData("tests")]
    [InlineData("avalonia")]
    public void EmptyProjectGraphIsRejected(string rule)
    {
        using var workspace = new TempWorkspace();
        workspace.PathFor("src/.keep");
        Assert.Single(ProjectRules.Check(workspace.Root, rule));
    }

    [Theory]
    [InlineData("ForbiddenDirection.csproj.txt", "direction")]
    [InlineData("ProductionTests.csproj.txt", "tests")]
    [InlineData("ForbiddenDirection.csproj.txt", "avalonia")]
    [InlineData("AvaloniaPackage.csproj.txt", "avalonia")]
    public void ForbiddenReferencesAreRejected(string fixture, string rule)
    {
        using var workspace = new TempWorkspace();
        workspace.UseFixture(fixture, "src/Nvt.Replay.Core/Nvt.Replay.Core.csproj");
        Assert.Single(ProjectRules.Check(workspace.Root, rule));
    }
}
