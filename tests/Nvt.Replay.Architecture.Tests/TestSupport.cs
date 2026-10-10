// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Nvt.Replay.Architecture.Tests;

internal static class RepositoryFiles
{
    internal static string Root => FindRepositoryRoot();
    internal static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Nvt.EventBufferReplay.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot find the NFU repository root.");
    }

    internal static JsonObject ReadJson(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonNode.Parse(stream)?.AsObject() ?? throw new InvalidDataException(path);
    }

    internal static void WriteJson(string path, JsonNode value) =>
        File.WriteAllText(path, value.ToJsonString(new() { WriteIndented = true }) + "\n");
}

internal sealed class TempWorkspace : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "nfu-architecture-" + Guid.NewGuid().ToString("N"));
    internal TempWorkspace() => Directory.CreateDirectory(Root);

    internal string PathFor(string relative)
    {
        var path = Path.GetFullPath(relative, Root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, comparison))
            throw new ArgumentException("Workspace path escapes its root.", nameof(relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    internal void UseFixture(string name, string destination) => File.Copy(
        Path.Combine(RepositoryFiles.Root, "tests/Nvt.Replay.Architecture.Tests/Fixtures", name), PathFor(destination), true);

    public void Dispose()
    {
        // Root is an immutable direct child of the system temporary directory.
        if (Path.GetDirectoryName(Root) != Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
            throw new IOException($"Unsafe cleanup target: {Root}");
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var file in Directory.EnumerateFiles(Root, "*", options))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(Root, true);
        }
        catch (IOException error) { throw new IOException($"Workspace cleanup failed: {Root}", error); }
        catch (UnauthorizedAccessException error) { throw new IOException($"Workspace cleanup failed: {Root}", error); }
    }
}

internal sealed record ChildResult(int ExitCode, string Output);

internal static class ChildProcessFixture
{
    private const int StreamLimit = 32_768;

    internal static async Task<ChildResult> RunAsync(string root, string executable, params string[] arguments)
    {
        using var watchdog = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // Each child gets its own environment. The test process is never changed.
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["GIT_CONFIG_COUNT"] = "1";
        start.Environment["GIT_CONFIG_KEY_0"] = "safe.directory";
        start.Environment["GIT_CONFIG_VALUE_0"] = root;
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = DrainAsync(process.StandardOutput, watchdog.Token);
        var stderr = DrainAsync(process.StandardError, watchdog.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(watchdog.Token), stdout, stderr).WaitAsync(watchdog.Token);
            return new(process.ExitCode, await stdout + await stderr);
        }
        catch (OperationCanceledException error)
        {
            throw new TimeoutException($"Child watchdog expired: {executable}", error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken token)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            output.Append(buffer, 0, Math.Min(read, StreamLimit - output.Length));
        return output.ToString();
    }

    internal static Task<ChildResult> ScriptAsync(string root, string script, params string[] arguments) =>
        RunAsync(root, "pwsh", ["-NoProfile", "-NonInteractive", "-File", Path.Combine(RepositoryFiles.Root, script), .. arguments]);

    internal static async Task CheckedAsync(string root, string executable, params string[] arguments)
    {
        var result = await RunAsync(root, executable, arguments);
        if (result.ExitCode != 0) throw new InvalidOperationException(result.Output);
    }
}
