// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Nvt.Core.Threading;

namespace Nvt.Replay.Avalonia.ViewModels;

internal sealed record ReplayShellSeams(
    Func<Task<string?>> PickSource,
    Func<string, Task> OpenCapture,
    Func<Task> DecodeCapture,
    Action Cancel,
    Func<bool> IsBusy,
    Func<bool> IsExportActive);

internal sealed class ReplayShellViewModel
{
    private readonly CaptureWorkspaceViewModel _capture;
    private readonly ReplayShellSeams _seams;
    private readonly UiEventRunner? _uiEvents;
    // UI thread only. Guards direct ExecuteAsync calls as well as ICommand entry points.
    private ICommand? _executingCommand;

    internal ReplayShellViewModel(CaptureWorkspaceViewModel capture, ReplayShellSeams seams, UiEventRunner? uiEvents = null)
    {
        _capture = capture;
        _seams = seams;
        _uiEvents = uiEvents;
        LoadCommand = new AsyncRelayCommand(() => ObserveAsync("capture.load", LoadAsync), CanLoad);
        DecodeCommand = new AsyncRelayCommand(() => ObserveAsync("capture.decode", DecodeAsync), CanDecode);
        CancelCommand = new RelayCommand(Cancel, () => _seams.IsBusy());
    }

    internal AsyncRelayCommand LoadCommand { get; }
    internal AsyncRelayCommand DecodeCommand { get; }
    internal RelayCommand CancelCommand { get; }
    internal bool IsExecuting => _executingCommand is not null;

    internal Task ExecuteAsync(IAsyncRelayCommand command) => command.CanExecute(null)
        ? command.ExecuteAsync(null)
        : Task.CompletedTask;

    internal bool TryExecuteLoad()
    {
        if (!LoadCommand.CanExecute(null)) return false;
        LoadCommand.Execute(null);
        return true;
    }

    internal void RefreshAvailability()
    {
        LoadCommand.NotifyCanExecuteChanged();
        DecodeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private bool CanLoad() => _executingCommand is null && !_seams.IsBusy() && !_seams.IsExportActive();
    private bool CanDecode() => _executingCommand is null && !_seams.IsBusy() && _capture.State is not CaptureState.Empty;

    private Task ObserveAsync(string operation, Func<Task> action) =>
        _uiEvents is { } runner ? runner.RunAsync(operation, _ => action(), CancellationToken.None) : action();

    private async Task LoadAsync()
    {
        if (!CanLoad()) return;
        _executingCommand = LoadCommand;
        RefreshAvailability();
        try
        {
            var path = await _seams.PickSource();
            if (path is not null) await _seams.OpenCapture(path);
        }
        finally
        {
            _executingCommand = null;
            RefreshAvailability();
        }
    }

    private async Task DecodeAsync()
    {
        if (!CanDecode()) return;
        _executingCommand = DecodeCommand;
        RefreshAvailability();
        try
        {
            await _seams.DecodeCapture();
        }
        finally
        {
            _executingCommand = null;
            RefreshAvailability();
        }
    }

    private void Cancel()
    {
        if (CancelCommand.CanExecute(null)) _seams.Cancel();
    }
}
