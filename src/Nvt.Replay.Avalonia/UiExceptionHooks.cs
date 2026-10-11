// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Avalonia.Threading;

namespace Nvt.Replay.Avalonia;

internal sealed record UiExceptionHooksSeams(
    Action<DispatcherUnhandledExceptionEventHandler> SubscribeDispatcher,
    Action<DispatcherUnhandledExceptionEventHandler> UnsubscribeDispatcher,
    Action<EventHandler<UnobservedTaskExceptionEventArgs>> SubscribeTasks,
    Action<EventHandler<UnobservedTaskExceptionEventArgs>> UnsubscribeTasks,
    Action<Action> PostToUi,
    Func<UiFailureReporter?> GetReporter);

internal sealed class UiExceptionHooks : IDisposable
{
    private readonly UiExceptionHooksSeams _seams;
    // UI thread only: registration and disposal follow the application lifetime.
    private bool _disposed;

    internal UiExceptionHooks(UiExceptionHooksSeams seams)
    {
        _seams = seams;
        _seams.SubscribeDispatcher(OnDispatcherException);
        _seams.SubscribeTasks(OnTaskException);
    }

    internal static UiExceptionHooks Register(Func<UiFailureReporter?> getReporter) => new(new(
        handler => Dispatcher.UIThread.UnhandledException += handler,
        handler => Dispatcher.UIThread.UnhandledException -= handler,
        handler => TaskScheduler.UnobservedTaskException += handler,
        handler => TaskScheduler.UnobservedTaskException -= handler,
        action => Dispatcher.UIThread.Post(action),
        getReporter));

    private void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report("dispatcher.unhandled", e.Exception);
        // Observe only. In particular, a startup failure must retain Avalonia's crash behavior.
    }

    private void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            // This event can run on the finalizer thread; window access belongs to the UI thread.
            _seams.PostToUi(() => Report("task.unobserved", e.Exception));
        }
        catch (Exception)
        {
            // A stopped dispatcher cannot accept a post. Preserve the original exception in diagnostics.
            UiFailureReporter.TraceFailure("task.unobserved", e.Exception);
        }
    }

    private void Report(string operation, Exception exception)
    {
        try
        {
            if (_seams.GetReporter() is { } reporter)
                reporter.ObserveFailure(operation, exception);
            else
                UiFailureReporter.TraceFailure(operation, exception);
        }
        catch (Exception)
        {
            // Lookup can fail during startup or shutdown; the global observer must not throw.
            UiFailureReporter.TraceFailure(operation, exception);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _seams.UnsubscribeDispatcher(OnDispatcherException);
        _seams.UnsubscribeTasks(OnTaskException);
    }
}
