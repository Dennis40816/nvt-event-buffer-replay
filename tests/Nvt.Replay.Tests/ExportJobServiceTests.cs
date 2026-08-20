using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ExportJobServiceTests
{
    [Fact]
    public async Task Runs_on_a_background_thread_reports_progress_and_allows_only_one_active_job()
    {
        var service = new ExportJobService();
        var callerContext = new SynchronizationContext();
        SynchronizationContext? operationContext = callerContext;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<ExportJobSnapshot>();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(callerContext);
        ExportJobHandle handle;
        try
        {
            handle = service.Start(async (_, progress) =>
            {
                operationContext = SynchronizationContext.Current;
                progress.Report(new ReplayExportProgress(4, 10, "Encoding MP4"));
                started.SetResult();
                await release.Task;
                return Result("first.mp4");
            }, new InlineProgress<ExportJobSnapshot>(updates.Add));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        await started.Task;
        Assert.True(service.IsActive);
        Assert.Null(operationContext);
        Assert.Equal("Encoding MP4", service.Snapshot.Progress?.Stage);
        Assert.Throws<InvalidOperationException>(() => service.Start((_, _) => Task.FromResult(Result("second.mp4"))));

        release.SetResult();
        var completed = await handle.Completion;

        Assert.Equal(ExportJobStatus.Succeeded, completed.Status);
        Assert.Equal("first.mp4", completed.Result?.OutputPath);
        Assert.False(service.IsActive);
        Assert.Contains(updates, item => item.Status == ExportJobStatus.Running && item.Progress?.CompletedFrames == 4);
        Assert.Equal(ExportJobStatus.Succeeded, updates[^1].Status);
    }

    [Fact]
    public async Task Cancel_is_idempotent_and_waits_for_a_cooperative_job_to_finish()
    {
        var service = new ExportJobService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = service.Start(async (cancellationToken, _) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result("unreachable.mp4");
        });
        await started.Task;

        Assert.True(service.Cancel());
        Assert.False(service.Cancel());
        Assert.True(service.IsActive);
        Assert.Equal(ExportJobStatus.Cancelling, service.Snapshot.Status);

        var completed = await handle.Completion;
        Assert.Equal(ExportJobStatus.Cancelled, completed.Status);
        Assert.Null(completed.Result);
        Assert.False(service.IsActive);
    }

    [Fact]
    public async Task Cancellation_callbacks_can_read_service_state_without_running_under_the_service_lock()
    {
        var service = new ExportJobService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ExportJobSnapshot? callbackSnapshot = null;
        var handle = service.Start(async (cancellationToken, _) =>
        {
            using var registration = cancellationToken.Register(() =>
            {
                callbackSnapshot = Task.Run(() => service.Snapshot)
                    .WaitAsync(TimeSpan.FromSeconds(1))
                    .GetAwaiter()
                    .GetResult();
            });
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Result("unreachable.mp4");
        });
        await started.Task;

        Assert.True(service.Cancel());
        var completed = await handle.Completion;

        Assert.Equal(ExportJobStatus.Cancelling, callbackSnapshot?.Status);
        Assert.Equal(ExportJobStatus.Cancelled, completed.Status);
    }

    [Fact]
    public async Task A_result_returned_after_cancellation_is_suppressed()
    {
        var service = new ExportJobService();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = service.Start(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return Result("stale.mp4");
        });
        await started.Task;

        Assert.True(service.Cancel());
        release.SetResult();
        var completed = await handle.Completion;

        Assert.Equal(ExportJobStatus.Cancelled, completed.Status);
        Assert.Null(completed.Result);
        Assert.Equal(ExportJobStatus.Cancelled, service.Snapshot.Status);
    }

    [Fact]
    public async Task Late_progress_from_an_old_job_cannot_replace_the_new_job_snapshot()
    {
        var service = new ExportJobService();
        IProgress<ReplayExportProgress>? oldProgress = null;
        var first = service.Start((_, progress) =>
        {
            oldProgress = progress;
            return Task.FromResult(Result("first.mp4"));
        });
        await first.Completion;

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = service.Start(async (cancellationToken, _) =>
        {
            secondStarted.SetResult();
            await secondRelease.Task.WaitAsync(cancellationToken);
            return Result("second.mp4");
        });
        await secondStarted.Task;

        oldProgress!.Report(new ReplayExportProgress(99, 100, "Stale completion"));

        Assert.Equal(second.JobId, service.Snapshot.JobId);
        Assert.NotEqual("Stale completion", service.Snapshot.Progress?.Stage);
        secondRelease.SetResult();
        Assert.Equal(ExportJobStatus.Succeeded, (await second.Completion).Status);
    }

    [Fact]
    public async Task Failure_is_observable_and_does_not_block_the_next_job()
    {
        var service = new ExportJobService();
        var failure = service.Start((_, _) => throw new IOException("disk unavailable"));

        var failed = await failure.Completion;

        Assert.Equal(ExportJobStatus.Failed, failed.Status);
        Assert.IsType<IOException>(failed.Error);
        Assert.False(service.IsActive);

        var next = service.Start((_, _) => Task.FromResult(Result("recovered.mp4")));
        var succeeded = await next.Completion;
        Assert.True(next.JobId > failure.JobId);
        Assert.Equal(ExportJobStatus.Succeeded, succeeded.Status);
    }

    [Fact]
    public async Task Observer_failures_do_not_fail_the_export()
    {
        var service = new ExportJobService();
        var observer = new InlineProgress<ExportJobSnapshot>(_ => throw new InvalidOperationException("UI closed"));

        var handle = service.Start((_, progress) =>
        {
            progress.Report(new ReplayExportProgress(1, 1, "Done"));
            return Task.FromResult(Result("safe.mp4"));
        }, observer);

        Assert.Equal(ExportJobStatus.Succeeded, (await handle.Completion).Status);
    }

    private static ReplayExportResult Result(string path) => new(
        ReplayExportKind.Mp4,
        path,
        path + ".json",
        1,
        TimeSpan.FromSeconds(1),
        null);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
