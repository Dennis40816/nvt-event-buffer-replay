// Copyright (c) 2026 Dennis Liu. All rights reserved.
namespace Nvt.Replay.Architecture.Tests;

internal static class HealthConfigurationFixture
{
    internal const string PackageScript = "scripts/check-central-packages.ps1";

    internal static async Task PreparePackagesAsync(TempWorkspace workspace)
    {
        Copy(workspace, "Directory.Packages.props");
        workspace.UseFixture("CentralPackages.csproj.txt", "src/Example/Example.csproj");
        var control = await PackagesAsync(workspace.Root);
        if (control.ExitCode != 0) throw new InvalidOperationException(control.Output);
    }

    private static void Copy(TempWorkspace workspace, string relative)
    {
        using var source = File.OpenRead(Path.Combine(RepositoryFiles.Root, relative));
        using var destination = File.Create(workspace.PathFor(relative));
        source.CopyTo(destination);
    }

    internal static Task<ChildResult> PackagesAsync(string root) =>
        ChildProcessFixture.ScriptAsync(root, PackageScript, "-Root", root);
}

public sealed class CentralPackageTests
{
    [Fact]
    public async Task CurrentProjectsPassCentralPackageCheckAsync()
    {
        var result = await HealthConfigurationFixture.PackagesAsync(RepositoryFiles.Root);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task VersionlessPackageReferencePassesCentralPackageCheckAsync()
    {
        using var workspace = new TempWorkspace();
        await HealthConfigurationFixture.PreparePackagesAsync(workspace);

        var result = await HealthConfigurationFixture.PackagesAsync(workspace.Root);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Theory]
    [InlineData("PackageVersionAttribute.csproj.txt")]
    [InlineData("PackageVersionElement.csproj.txt")]
    [InlineData("PackageVersionOverride.csproj.txt")]
    public async Task LocalPackageVersionFailsCentralPackageCheckAsync(string fixture)
    {
        using var workspace = new TempWorkspace();
        await HealthConfigurationFixture.PreparePackagesAsync(workspace);
        workspace.UseFixture(fixture, "src/Example/Example.csproj");

        var result = await HealthConfigurationFixture.PackagesAsync(workspace.Root);

        Assert.True(result.ExitCode == 1 && result.Output.Contains("HC_CPM", StringComparison.Ordinal), result.Output);
    }
}
