using System.Diagnostics;

namespace Nvt.Replay.Avalonia;

internal sealed record SourceFileOpenResult(
    bool Opened,
    bool ExactLine,
    string Application,
    string? Error = null);

internal static class SourceFileNavigator
{
    public static SourceFileOpenResult Open(string sourcePath, int lineNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var path = Path.GetFullPath(sourcePath);
        if (!File.Exists(path))
            return new SourceFileOpenResult(false, false, string.Empty, $"File not found: {path}");

        foreach (var codePath in VisualStudioCodePaths())
        {
            if (!File.Exists(codePath)) continue;
            if (TryStart(codePath, ["--reuse-window", "--goto", $"{path}:{Math.Max(1, lineNumber)}:1"], out var error))
                return new SourceFileOpenResult(true, true, "Visual Studio Code");
            if (!string.IsNullOrWhiteSpace(error))
                return new SourceFileOpenResult(false, false, "Visual Studio Code", error);
        }

        foreach (var notepadPlusPlusPath in NotepadPlusPlusPaths())
        {
            if (!File.Exists(notepadPlusPlusPath)) continue;
            if (TryStart(notepadPlusPlusPath, [$"-n{Math.Max(1, lineNumber)}", path], out var error))
                return new SourceFileOpenResult(true, true, "Notepad++");
            if (!string.IsNullOrWhiteSpace(error))
                return new SourceFileOpenResult(false, false, "Notepad++", error);
        }

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

    private static bool TryStart(string executable, IReadOnlyList<string> arguments, out string? error)
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

    private static IEnumerable<string> VisualStudioCodePaths()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe");
        yield return Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe");
        yield return Path.Combine(programFiles, "Microsoft VS Code", "Code.exe");
        yield return Path.Combine(programFilesX86, "Microsoft VS Code", "Code.exe");
    }

    private static IEnumerable<string> NotepadPlusPlusPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(programFiles, "Notepad++", "notepad++.exe");
        yield return Path.Combine(programFilesX86, "Notepad++", "notepad++.exe");
    }
}
