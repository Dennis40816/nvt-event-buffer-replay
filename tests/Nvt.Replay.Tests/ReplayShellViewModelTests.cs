// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Windows.Input;
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Tests;

public sealed class ReplayShellViewModelTests
{
    [Fact]
    public void EmptyCaptureAllowsLoad()
    {
        var shell = Create(new CaptureWorkspaceViewModel());

        Assert.True(shell.LoadCommand.CanExecute(null));
    }

    [Fact]
    public void EmptyCaptureDisablesDecode()
    {
        var shell = Create(new CaptureWorkspaceViewModel());

        Assert.False(shell.DecodeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProbedCaptureAllowsDecode()
    {
        var capture = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.ProbeAsync(capture);
        var shell = Create(capture);

        Assert.True(shell.DecodeCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearingCaptureDisablesDecode()
    {
        var capture = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.ProbeAsync(capture);
        var shell = Create(capture);

        capture.Clear();

        Assert.False(shell.DecodeCommand.CanExecute(null));
    }

    [Fact]
    public void BusyOperationDisablesLoad()
    {
        var shell = Create(new CaptureWorkspaceViewModel(), busy: () => true);

        Assert.False(shell.LoadCommand.CanExecute(null));
    }

    [Fact]
    public async Task BusyOperationDisablesDecode()
    {
        var capture = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.ProbeAsync(capture);
        var shell = Create(capture, busy: () => true);

        Assert.False(shell.DecodeCommand.CanExecute(null));
    }

    [Fact]
    public void ExportDisablesLoad()
    {
        var shell = Create(new CaptureWorkspaceViewModel(), exporting: () => true);

        Assert.False(shell.LoadCommand.CanExecute(null));
    }

    [Fact]
    public void IdleOperationDisablesCancel()
    {
        var shell = Create(new CaptureWorkspaceViewModel());

        Assert.False(shell.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void BusyOperationAllowsCancel()
    {
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), cancel: () => calls++, busy: () => true);

        shell.CancelCommand.Execute(null);

        Assert.True(shell.CancelCommand.CanExecute(null));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DirectCancelInvocationCannotBypassGuard()
    {
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), cancel: () => calls++);

        shell.CancelCommand.Execute(null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PickerCancelKeepsDisplayedCapture()
    {
        var capture = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.DecodeAsync(capture);
        var displayed = capture.State;
        var opens = 0;
        var shell = Create(capture, pick: () => Task.FromResult((string?)null), open: _ =>
        {
            opens++;
            return Task.CompletedTask;
        });

        await shell.ExecuteAsync(shell.LoadCommand);

        Assert.Same(displayed, capture.State);
        Assert.Equal(0, opens);
        Assert.False(shell.IsExecuting);
    }

    [Fact]
    public async Task LoadPassesSelectedPathOnce()
    {
        string? opened = null;
        var shell = Create(new CaptureWorkspaceViewModel(), open: path =>
        {
            opened = path;
            return Task.CompletedTask;
        });

        await shell.ExecuteAsync(shell.LoadCommand);

        Assert.Equal("capture.csv", opened);
    }

    [Fact]
    public async Task PendingPickerDisablesDuplicateLoad()
    {
        var picker = CaptureTestSupport.Signal<string?>();
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), pick: () =>
        {
            calls++;
            return picker.Task;
        });
        var first = shell.ExecuteAsync(shell.LoadCommand);

        await shell.ExecuteAsync(shell.LoadCommand);

        Assert.True(shell.IsExecuting);
        Assert.True(shell.LoadCommand.IsRunning);
        Assert.False(shell.LoadCommand.CanExecute(null));
        Assert.Equal(1, calls);
        picker.SetResult(null);
        await first;
        Assert.False(shell.IsExecuting);
    }

    [Fact]
    public async Task DirectDuplicateInvocationDoesNotOpenSecondPicker()
    {
        var picker = CaptureTestSupport.Signal<string?>();
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), pick: () =>
        {
            calls++;
            return picker.Task;
        });
        var first = shell.LoadCommand.ExecuteAsync(null);

        await shell.LoadCommand.ExecuteAsync(null);

        Assert.Equal(1, calls);
        Assert.True(shell.IsExecuting);
        picker.SetResult(null);
        await first;
    }

    [Fact]
    public async Task DecodeCommandTracksExecution()
    {
        var capture = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.ProbeAsync(capture);
        var completion = CaptureTestSupport.Signal<bool>();
        var shell = Create(capture, decode: () => completion.Task);
        var decode = shell.ExecuteAsync(shell.DecodeCommand);

        Assert.True(shell.IsExecuting);
        Assert.True(shell.DecodeCommand.IsRunning);
        Assert.False(shell.LoadCommand.CanExecute(null));
        Assert.False(shell.DecodeCommand.CanExecute(null));
        completion.SetResult(true);
        await decode;
        Assert.True(shell.DecodeCommand.CanExecute(null));
    }

    [Fact]
    public async Task DuplicateDecodeExecutesOnce()
    {
        var capture = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.ProbeAsync(capture);
        var completion = CaptureTestSupport.Signal<bool>();
        var calls = 0;
        var shell = Create(capture, decode: () =>
        {
            calls++;
            return completion.Task;
        });
        var first = shell.ExecuteAsync(shell.DecodeCommand);

        await shell.ExecuteAsync(shell.DecodeCommand);

        Assert.Equal(1, calls);
        completion.SetResult(true);
        await first;
    }

    [Fact]
    public async Task DirectDecodeInvocationCannotBypassEmptyGuard()
    {
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), decode: () =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await shell.DecodeCommand.ExecuteAsync(null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DirectLoadInvocationCannotBypassBusyGuard()
    {
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), pick: () =>
        {
            calls++;
            return Task.FromResult((string?)null);
        }, busy: () => true);

        await shell.LoadCommand.ExecuteAsync(null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DirectLoadInvocationCannotBypassExportGuard()
    {
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), pick: () =>
        {
            calls++;
            return Task.FromResult((string?)null);
        }, exporting: () => true);

        await shell.LoadCommand.ExecuteAsync(null);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task FailedLoadReleasesCommandExecution()
    {
        var shell = Create(new CaptureWorkspaceViewModel(), open: _ => Task.FromException(new IOException("synthetic failure")));

        await Assert.ThrowsAsync<IOException>(() => shell.ExecuteAsync(shell.LoadCommand));

        Assert.False(shell.IsExecuting);
        Assert.True(shell.LoadCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("button")]
    [InlineData("menu")]
    [InlineData("shortcut")]
    public async Task LoadEntryPointsShareAvailabilityGuard(string entryPoint)
    {
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), pick: () =>
        {
            calls++;
            return Task.FromResult((string?)null);
        }, busy: () => true);

        await InvokeLoadAsync(shell, entryPoint);

        Assert.False(shell.LoadCommand.CanExecute(null));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("button")]
    [InlineData("menu")]
    [InlineData("shortcut")]
    public async Task LoadEntryPointsExecuteWhenAvailable(string entryPoint)
    {
        var calls = 0;
        var shell = Create(new CaptureWorkspaceViewModel(), pick: () =>
        {
            calls++;
            return Task.FromResult((string?)null);
        });

        await InvokeLoadAsync(shell, entryPoint);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void AvailabilityChangeNotifiesCommandConsumers()
    {
        var notices = 0;
        var shell = Create(new CaptureWorkspaceViewModel());
        shell.LoadCommand.CanExecuteChanged += (_, _) => notices++;

        shell.RefreshAvailability();

        Assert.Equal(1, notices);
    }

    private static Task InvokeLoadAsync(ReplayShellViewModel shell, string entryPoint)
    {
        // These are the async button adapter and the shared synchronous palette/shortcut adapter.
        return entryPoint switch
        {
            "button" => shell.ExecuteAsync(shell.LoadCommand),
            "menu" or "shortcut" => InvokeShortcut(shell),
            _ => throw new ArgumentOutOfRangeException(nameof(entryPoint)),
        };
    }

    private static Task InvokeShortcut(ReplayShellViewModel shell)
    {
        shell.TryExecuteLoad();
        return shell.LoadCommand.ExecutionTask ?? Task.CompletedTask;
    }

    private static ReplayShellViewModel Create(
        CaptureWorkspaceViewModel capture,
        Func<Task<string?>>? pick = null,
        Func<string, Task>? open = null,
        Func<Task>? decode = null,
        Action? cancel = null,
        Func<bool>? busy = null,
        Func<bool>? exporting = null) => new(capture, new ReplayShellSeams(
            pick ?? (() => Task.FromResult((string?)"capture.csv")),
            open ?? (_ => Task.CompletedTask),
            decode ?? (() => Task.CompletedTask),
            cancel ?? (() => { }),
            busy ?? (() => false),
            exporting ?? (() => false)));
}
