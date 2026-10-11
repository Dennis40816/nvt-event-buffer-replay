// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Avalonia;
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Tests;

public sealed class UiFailureReporterTests
{
    [Fact]
    public void SynchronousEventFailureReportsOperationOnce()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();
        void Handler(object? sender, EventArgs args) =>
            reporter.Runner.Run("capture.load", _ => ThrowBeforeReturningTask(), CancellationToken.None);

        Handler(null, EventArgs.Empty);

        Assert.Equal(("capture.load", "Load failed · picker failed"), Assert.Single(probe.Reports));
    }

    [Fact]
    public async Task DeferredTaskFailureReportsOperationOnce()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();
        var operation = CaptureTestSupport.Signal<bool>();
        var observed = reporter.Runner.RunAsync("capture.load", _ => operation.Task);

        operation.SetException(new InvalidOperationException("picker failed"));
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await observed.WaitAsync(watchdog.Token);

        Assert.Equal(("capture.load", "Load failed · picker failed"), Assert.Single(probe.Reports));
    }

    [Fact]
    public async Task SuppliedTokenCancellationReportsNothing()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await reporter.Runner.RunAsync("capture.load", _ => Task.FromCanceled(cancellation.Token), cancellation.Token);

        Assert.Empty(probe.Reports);
        Assert.Empty(probe.Fallbacks);
    }

    [Fact]
    public async Task DifferentTokenCancellationIsReported()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();
        using var supplied = new CancellationTokenSource();
        using var different = new CancellationTokenSource();
        supplied.Cancel();
        different.Cancel();

        await reporter.Runner.RunAsync("capture.load", _ => Task.FromCanceled(different.Token), supplied.Token);

        Assert.Equal("capture.load", Assert.Single(probe.Reports).Operation);
    }

    [Fact]
    public async Task PrimaryReporterFailureUsesFallbackWithOriginalException()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter(publish: (_, _) => throw new InvalidOperationException("status unavailable"));
        var original = new InvalidOperationException("picker failed");

        await reporter.Runner.RunAsync("capture.load", _ => Task.FromException(original));

        Assert.Equal(("capture.load", original), Assert.Single(probe.Fallbacks));
    }

    [Fact]
    public async Task ThrowingFallbackCannotEscapeRunner()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter(
            publish: (_, _) => throw new InvalidOperationException("status unavailable"),
            fallback: (_, _) => throw new InvalidOperationException("diagnostics unavailable"));

        await reporter.Runner.RunAsync("capture.load", _ => Task.FromException(new InvalidOperationException("picker failed")));
    }

    [Fact]
    public void ThrowingDiagnosticsCannotEscapeFallback()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter(fallback: (_, _) => throw new InvalidOperationException("listener failed"));

        var error = Record.Exception(() => reporter.ReportFallback("capture.load", new InvalidOperationException("picker failed")));

        Assert.Null(error);
    }

    [Fact]
    public async Task SuccessfulOperationLeavesFailureStatusAlone()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();

        await reporter.Runner.RunAsync("capture.load", _ => Task.CompletedTask);

        Assert.Empty(probe.Reports);
        Assert.Empty(probe.Fallbacks);
    }

    [Fact]
    public async Task FailureResetsBusyThroughHostCallback()
    {
        var busy = true;
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter(publish: (_, _) => busy = false);

        await reporter.Runner.RunAsync("capture.load", _ => Task.FromException(new InvalidOperationException("picker failed")));

        Assert.False(busy);
    }

    [Fact]
    public void BoundLoadCommandFailureIsObservedOnce()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();
        var shell = new ReplayShellViewModel(new(), new(
            () => Task.FromException<string?>(new InvalidOperationException("picker failed")),
            _ => Task.CompletedTask, () => Task.CompletedTask, () => { }, () => false, () => false), reporter.Runner);

        shell.LoadCommand.Execute(null);

        Assert.Equal(("capture.load", "Load failed · picker failed"), Assert.Single(probe.Reports));
    }

    [Fact]
    public async Task BoundDecodeCommandFailureIsObservedOnce()
    {
        var capture = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.ProbeAsync(capture);
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();
        var shell = new ReplayShellViewModel(capture, new(
            () => Task.FromResult((string?)null), _ => Task.CompletedTask,
            () => Task.FromException(new InvalidOperationException("decoder failed")),
            () => { }, () => false, () => false), reporter.Runner);

        await shell.ExecuteAsync(shell.DecodeCommand);

        Assert.Equal(("capture.decode", "Decode failed · decoder failed"), Assert.Single(probe.Reports));
    }

    [Theory]
    [InlineData("capture.source-adapter", "Load failed")]
    [InlineData("capture.register-profile", "Register profile failed")]
    [InlineData("capture.export-readable-log", "Readable log export failed")]
    [InlineData("output.export-points", "Point plot export failed")]
    [InlineData("output.export-heatmap", "Heatmap export failed")]
    [InlineData("output.export-analysis", "Analysis export failed")]
    [InlineData("output.export-replay", "Replay export failed")]
    [InlineData("output.export-continue", "Replay export failed")]
    [InlineData("review.attach-evidence", "Evidence attach failed")]
    [InlineData("review.save", "Review save failed")]
    [InlineData("review.load", "Review open failed")]
    public void ExistingFailureLabelsArePreserved(string operation, string label)
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter();

        reporter.Report(operation, new InvalidOperationException("failed"));

        Assert.Equal((operation, label + " · failed"), Assert.Single(probe.Reports));
    }

    [Theory]
    [InlineData("output.preview-play-pause", 1, 0)]
    [InlineData("capture.apply-setup", 0, 1)]
    [InlineData("review.copy-inspector", 0, 0)]
    public async Task FailureRunsOnlyItsOperationCleanup(string operation, int expectedPreview, int expectedSetup)
    {
        var previewResets = 0;
        var setupRefreshes = 0;
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter(resetPreview: () => previewResets++, refreshSetup: () => setupRefreshes++);

        await reporter.Runner.RunAsync(operation, _ => Task.FromException(new InvalidOperationException("failed")));

        Assert.Equal((expectedPreview, expectedSetup), (previewResets, setupRefreshes));
    }

    [Fact]
    public async Task CleanupFailureReportsOriginalExceptionToFallback()
    {
        var probe = new UiFailureTestProbe();
        var reporter = probe.CreateReporter(refreshSetup: () => throw new InvalidOperationException("validation unavailable"));
        var original = new InvalidOperationException("setup failed");

        await reporter.Runner.RunAsync("capture.apply-setup", _ => Task.FromException(original));

        Assert.Equal(("capture.apply-setup", original), Assert.Single(probe.Fallbacks));
    }

    private static Task ThrowBeforeReturningTask() => throw new InvalidOperationException("picker failed");
}
