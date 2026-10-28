using System.Globalization;
using System.Text;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class ExportJobTraceBaselineTests
{
    [Fact]
    public async Task Capture_export_job_trace_when_opted_in()
    {
        var directory = Environment.GetEnvironmentVariable("NVT_PROGRESS_BASELINE_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;

        var first = await TraceAsync();
        Assert.Equal(first, await TraceAsync());
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "export-job-trace.txt"), first, new UTF8Encoding(false));
    }

    private static async Task<string> TraceAsync()
    {
        var trace = new StringBuilder();
        foreach (var scenario in new[]
                 {
                     "success-known", "success-unknown", "cancel-running", "late-result",
                     "late-progress", "failure", "second-start", "throwing-observer", "cancel-idempotence"
                 })
        {
            var service = new ExportJobService();
            var cancel = scenario is "cancel-running" or "late-result" or "late-progress" or "cancel-idempotence";
            var cooperative = scenario is "cancel-running" or "cancel-idempotence";
            ReplayExportProgress[] reports = scenario switch
            {
                "success-known" => [new(0, 10, "Encoding MP4"), new(5, 10, "Encoding MP4"), new(10, 10, "Encoding MP4")],
                "success-unknown" => [new(7, 0, "Encoding MP4")],
                "late-progress" => [new(9, 10, "Late progress")],
                "throwing-observer" => [new(10, 10, "Encoding MP4")],
                _ => []
            };
            var begin = Gate();
            var entered = Gate();
            var cancellation = Gate();
            var finish = Gate();
            var release = reports.Select(_ => Gate()).ToArray();
            var reported = reports.Select(_ => Gate()).ToArray();

            void Record(string eventName, ExportJobSnapshot? snapshot = null, ExportJobHandle? handle = null,
                bool? cancelReturn = null, Exception? startError = null)
            {
                snapshot ??= service.Snapshot;
                trace.Append(scenario).Append('|').Append(eventName)
                    .Append("|job=").Append(Text(snapshot.JobId))
                    .Append("|status=").Append(Text(snapshot.Status))
                    .Append("|completed-frames=").Append(Text(snapshot.Progress?.CompletedFrames))
                    .Append("|total-frames=").Append(Text(snapshot.Progress?.TotalFrames))
                    .Append("|stage=").Append(Text(snapshot.Progress?.Stage))
                    .Append("|kind=").Append(Text(snapshot.Result?.Kind))
                    .Append("|output-path=").Append(Text(snapshot.Result?.OutputPath))
                    .Append("|manifest-path=").Append(Text(snapshot.Result?.ManifestPath))
                    .Append("|output-frame-count=").Append(Text(snapshot.Result?.OutputFrameCount))
                    .Append("|duration=").Append(Text(snapshot.Result?.Duration))
                    .Append("|warning=").Append(Text(snapshot.Result?.Warning))
                    .Append("|error-type=").Append(Text(snapshot.Error?.GetType().FullName))
                    .Append("|error-message=").Append(Text(snapshot.Error?.Message))
                    .Append("|handle-job=").Append(Text(handle?.JobId))
                    .Append("|is-active=").Append(Text(service.IsActive))
                    .Append("|snapshot-is-active=").Append(Text(snapshot.IsActive))
                    .Append("|cancel-return=").Append(Text(cancelReturn))
                    .Append("|start-error-type=").Append(Text(startError?.GetType().FullName))
                    .Append("|start-error-message=").Append(Text(startError?.Message)).Append('\n');
            }

            var observer = new InlineProgress<ExportJobSnapshot>(snapshot =>
            {
                Record("observer", snapshot);
                if (scenario == "throwing-observer") throw new InvalidOperationException("baseline observer failure");
            });
            Record("initial");
            if (scenario == "cancel-idempotence") Record("cancel-idle", cancelReturn: service.Cancel());

            var handle = service.Start(async (token, progress) =>
            {
                await begin.Task;
                using var registration = token.Register(() => cancellation.TrySetResult());
                entered.SetResult();
                if (cooperative) await cancellation.Task;
                for (var index = 0; index < reports.Length; index++)
                {
                    await release[index].Task;
                    progress.Report(reports[index]);
                    reported[index].SetResult();
                }
                // Even cooperative cancellation cannot complete before its state has been recorded.
                await finish.Task;
                if (cooperative) token.ThrowIfCancellationRequested();
                if (scenario == "failure") throw new InvalidOperationException("baseline failure");
                return Result();
            }, observer);
            Record("after-start", handle: handle);
            begin.SetResult();
            await entered.Task;
            Record("after-operation-entered", handle: handle);

            if (scenario == "second-start")
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    service.Start((_, _) => Task.FromResult(Result()), observer));
                Record("start-threw", handle: handle, startError: error);
            }
            if (cancel)
            {
                Record("after-cancel", handle: handle, cancelReturn: service.Cancel());
                if (scenario == "cancel-idempotence")
                    Record("after-second-cancel", handle: handle, cancelReturn: service.Cancel());
            }
            for (var index = 0; index < reports.Length; index++)
            {
                release[index].SetResult();
                await reported[index].Task;
                Record($"after-progress-{index + 1}", handle: handle);
            }
            finish.SetResult();
            var completed = await handle.Completion;
            Record("completion", completed, handle);
            Record("after-completion", handle: handle);
            Assert.Equal(cancel ? ExportJobStatus.Cancelled : scenario == "failure"
                ? ExportJobStatus.Failed : ExportJobStatus.Succeeded, completed.Status);

            if (scenario == "second-start")
            {
                var nextFinish = Gate();
                var next = service.Start(async (_, _) =>
                {
                    await nextFinish.Task;
                    return Result();
                }, observer);
                Record("after-next-start", handle: next);
                Assert.Equal(handle.JobId + 1, next.JobId);
                nextFinish.SetResult();
                Record("next-completion", await next.Completion, next);
                Record("after-next-completion", handle: next);
            }
        }
        return trace.ToString();
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static ReplayExportResult Result() => new(
        ReplayExportKind.Mp4, "baseline.mp4", "baseline.manifest.json", 10,
        TimeSpan.FromMilliseconds(1250), "baseline warning");

    private static string Text(object? value) => (value switch
    {
        null => "-",
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!
    }).Replace("\\", "\\\\").Replace("|", "\\|").Replace("\r", "\\r").Replace("\n", "\\n");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
