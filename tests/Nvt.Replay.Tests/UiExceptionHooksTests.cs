// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Avalonia.Threading;
using Nvt.Replay.Avalonia;

namespace Nvt.Replay.Tests;

public sealed class UiExceptionHooksTests
{
    [Fact]
    public void RegistrationSubscribesBothGlobalEvents()
    {
        var probe = new HookProbe(new UiFailureTestProbe().CreateReporter());

        using var hooks = new UiExceptionHooks(probe.Seams);

        Assert.NotNull(probe.DispatcherHandler);
        Assert.NotNull(probe.TaskHandler);
    }

    [Fact]
    public void DisposalRemovesBothGlobalSubscriptions()
    {
        var probe = new HookProbe(new UiFailureTestProbe().CreateReporter());
        var hooks = new UiExceptionHooks(probe.Seams);

        hooks.Dispose();

        Assert.Null(probe.DispatcherHandler);
        Assert.Null(probe.TaskHandler);
    }

    [Fact]
    public void UnobservedFailureIsReportedThroughUiPost()
    {
        var failures = new UiFailureTestProbe();
        var probe = new HookProbe(failures.CreateReporter());
        using var hooks = new UiExceptionHooks(probe.Seams);
        var error = new AggregateException(new InvalidOperationException("background failed"));

        probe.TaskHandler!(null, new(error));
        Assert.Single(probe.Posts)();

        Assert.Equal(("task.unobserved", "UI operation task.unobserved failed · " + error.Message), Assert.Single(failures.Reports));
    }

    [Fact]
    public void ReporterLookupWaitsForUiPost()
    {
        var probe = new HookProbe(new UiFailureTestProbe().CreateReporter());
        using var hooks = new UiExceptionHooks(probe.Seams);

        probe.TaskHandler!(null, new(new AggregateException("background failed")));

        Assert.Equal(0, probe.LookupCount);
        Assert.Single(probe.Posts);
    }

    [Fact]
    public void GlobalObservationPreservesUnobservedExceptionPolicy()
    {
        var probe = new HookProbe(new UiFailureTestProbe().CreateReporter());
        using var hooks = new UiExceptionHooks(probe.Seams);
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException("background failed"));

        probe.TaskHandler!(null, args);
        Assert.Single(probe.Posts)();

        Assert.False(args.Observed);
    }

    [Fact]
    public void PostingFailureCannotEscapeGlobalObserver()
    {
        var probe = new HookProbe(new UiFailureTestProbe().CreateReporter());
        using var hooks = new UiExceptionHooks(probe.Seams with { PostToUi = _ => throw new InvalidOperationException("dispatcher stopped") });

        var error = Record.Exception(() => probe.TaskHandler!(null, new(new AggregateException("background failed"))));

        Assert.Null(error);
    }

    [Fact]
    public void MissingStartupWindowCannotBreakGlobalObserver()
    {
        var probe = new HookProbe(null);
        using var hooks = new UiExceptionHooks(probe.Seams);
        probe.TaskHandler!(null, new(new AggregateException("startup failed")));

        var error = Record.Exception(Assert.Single(probe.Posts));

        Assert.Null(error);
    }

    private sealed class HookProbe
    {
        private readonly UiFailureReporter? _reporter;
        internal DispatcherUnhandledExceptionEventHandler? DispatcherHandler { get; private set; }
        internal EventHandler<UnobservedTaskExceptionEventArgs>? TaskHandler { get; private set; }
        internal List<Action> Posts { get; } = [];
        internal int LookupCount { get; private set; }

        internal HookProbe(UiFailureReporter? reporter) => _reporter = reporter;

        internal UiExceptionHooksSeams Seams => new(
            handler => DispatcherHandler += handler, handler => DispatcherHandler -= handler,
            handler => TaskHandler += handler, handler => TaskHandler -= handler,
            Posts.Add, GetReporter);

        private UiFailureReporter? GetReporter()
        {
            LookupCount++;
            return _reporter;
        }
    }
}
