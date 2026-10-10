// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Xml.Linq;

namespace Nvt.Replay.Architecture.Tests;

internal static class InventoryPolicy
{
    internal static bool Matches(string committed, string generated) =>
        File.ReadAllBytes(committed).AsSpan().SequenceEqual(File.ReadAllBytes(generated));

    internal static string Describe(string committed, string generated)
    {
        var left = File.ReadAllText(committed).Split('\n');
        var right = File.ReadAllText(generated).Split('\n');
        var count = Math.Min(left.Length, right.Length);
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
                return $"First difference at line {i + 1}. Committed: [{left[i].TrimEnd('\r')}] Fresh: [{right[i].TrimEnd('\r')}]. Lines: {left.Length} and {right.Length}.";
        }
        return $"No line differs in the shared range. Lines: {left.Length} and {right.Length}.";
    }

    internal static async Task GenerateAsync(string root, string output)
    {
        var result = await ChildProcessFixture.ScriptAsync(root, "scripts/inventory-command-entries.ps1", "-Root", root, "-OutputPath", output);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }

    internal static string[] MissingDataTypes(string root, IEnumerable<string> exceptions)
    {
        var allowed = exceptions.ToHashSet(StringComparer.Ordinal);
        var dataType = XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml");
        return Directory.EnumerateFiles(Path.Combine(root, "src"), "*.axaml", SearchOption.AllDirectories)
            .Where(path => !allowed.Contains(Path.GetRelativePath(root, path).Replace('\\', '/')))
            .Where(path => XDocument.Load(path).Descendants().Any(element =>
                element.Attributes().Any(attribute => attribute.Value.StartsWith("{Binding", StringComparison.Ordinal)) &&
                !element.AncestorsAndSelf().Any(ancestor => ancestor.Attribute(dataType) is { Value.Length: > 0 })))
            .ToArray();
    }
}

public sealed class InventoryTests
{
    [Fact]
    public async Task CommandInventoryMatchesFreshRun()
    {
        using var workspace = new TempWorkspace();
        var output = workspace.PathFor("commands.json");
        await InventoryPolicy.GenerateAsync(RepositoryFiles.Root, output);
        var committed = Path.Combine(RepositoryFiles.Root, "eng/architecture/command-entries.json");
        Assert.True(InventoryPolicy.Matches(committed, output), InventoryPolicy.Describe(committed, output));
    }

    [Fact]
    public async Task AddingAHandlerMakesInventoryDrift()
    {
        using var workspace = new TempWorkspace();
        workspace.UseFixture("Commands.cs.txt", "src/Nvt.Replay.Avalonia/MainWindow.cs");
        workspace.UseFixture("Commands.axaml.txt", "src/Nvt.Replay.Avalonia/MainWindow.axaml");
        var before = workspace.PathFor("before.json");
        await InventoryPolicy.GenerateAsync(workspace.Root, before);
        workspace.UseFixture("CommandsAdded.axaml.txt", "src/Nvt.Replay.Avalonia/MainWindow.axaml");
        var after = workspace.PathFor("after.json");
        await InventoryPolicy.GenerateAsync(workspace.Root, after);
        Assert.False(InventoryPolicy.Matches(before, after));
    }

    [Fact]
    public void ProductionBindingExceptionsCoverCurrentFiles()
    {
        var baseline = RepositoryFiles.ReadJson(Path.Combine(RepositoryFiles.Root, "eng/architecture/compiled-binding-exceptions.json"));
        var files = baseline["files"]!.AsArray().Select(file => file!["file"]!.GetValue<string>());
        Assert.Empty(InventoryPolicy.MissingDataTypes(RepositoryFiles.Root, files));
    }

    [Fact]
    public void UnlistedBindingWithoutDataTypeIsRejected()
    {
        using var workspace = new TempWorkspace();
        workspace.UseFixture("Untyped.axaml.txt", "src/Nvt.Replay.Avalonia/NewView.axaml");
        Assert.Single(InventoryPolicy.MissingDataTypes(workspace.Root, []));
    }

    [Fact]
    public void UnlistedBindingWithDataTypeIsAllowed()
    {
        using var workspace = new TempWorkspace();
        workspace.UseFixture("Typed.axaml.txt", "src/Nvt.Replay.Avalonia/NewView.axaml");
        Assert.Empty(InventoryPolicy.MissingDataTypes(workspace.Root, []));
    }
}
