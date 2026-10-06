using System.Diagnostics;
using Nvt.Core.SourceFileNavigation;
using Xunit;
using CoreNavigator = Nvt.Core.SourceFileNavigation.SourceFileNavigator;

namespace Nvt.Replay.Avalonia.Tests;

// Zero-difference evidence for replacing NFU's process start and default open with Nvt.Core.SourceFileNavigation.
// Every input fails before a process starts: an empty name or a path inside a fresh, empty folder.
public sealed class SourceFileNavigatorAdoptionParityTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"nvt-navigator-parity-{Guid.NewGuid():N}");

    public SourceFileNavigatorAdoptionParityTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData("")]
    [InlineData("missing.exe")]
    [InlineData("missing editor.exe")]
    public void TryStart_returns_the_same_outcome(string executable)
    {
        var path = executable.Length == 0 ? executable : Path.Combine(directory, executable);
        string[] arguments = ["--goto", Path.Combine(directory, "a b.txt") + ":7:1", "-n7", "\"quoted\""];

        var oracleStarted = OracleTryStart(path, arguments, out var oracleError);
        var coreStarted = CoreNavigator.TryStart(path, arguments, out var coreError);

        Assert.False(oracleStarted);
        Assert.Equal(oracleStarted, coreStarted);
        Assert.NotNull(oracleError);
        Assert.Equal(oracleError, coreError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing.txt")]
    [InlineData("missing capture.csv")]
    public void OpenDefault_returns_the_same_outcome(string file)
    {
        var path = file.Length == 0 ? file : Path.Combine(directory, file);

        var oracle = OracleOpenDefault(path);
        var core = CoreNavigator.OpenDefault(path);

        Assert.False(oracle.Opened);
        Assert.NotNull(oracle.Error);
        Assert.Equal(oracle, core);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    public void Dispose()
    {
        Directory.Delete(directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    // Test-only oracle: verbatim copy of the private TryStart that this change deletes from
    // src/Nvt.Replay.Avalonia/SourceFileNavigator.cs (lines 134-153) at 26d66bd377a4ad051392bd7cc7e9d1c2e6287dba.
    private static bool OracleTryStart(string executable, IReadOnlyList<string> arguments, out string? error)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            Process.Start(startInfo);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            error = exception.Message;
            return false;
        }
    }

    // Test-only oracle: verbatim copy of the default-open block that this change replaces in
    // SourceFileNavigator.Open (lines 45-57 of the same file and commit). The NFU record had the same fields.
    private static SourceFileOpenResult OracleOpenDefault(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return new SourceFileOpenResult(true, false, "default application");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new SourceFileOpenResult(false, false, "default application", exception.Message);
        }
    }
}
