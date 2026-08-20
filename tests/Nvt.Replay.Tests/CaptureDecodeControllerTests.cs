using Nvt.Replay.Analysis;

namespace Nvt.Replay.Tests;

public sealed class CaptureDecodeControllerTests
{
    [Fact]
    public async Task New_operation_cancels_and_prevents_stale_first_publication()
    {
        var testCancellation = CancellationToken.None;
        using var controller = new CaptureDecodeController();
        using var blockingProgress = new BlockingProgress(
            testCancellation,
            CaptureDecodePhase.Ready);
        var request = CommonRequest();

        var first = controller.RunAsync(request, blockingProgress, testCancellation);
        Assert.True(blockingProgress.Entered.Wait(TimeSpan.FromSeconds(10), testCancellation));

        CaptureDecodeResult second;
        try
        {
            second = await controller.RunAsync(request, cancellationToken: testCancellation);
        }
        finally
        {
            blockingProgress.Release.Set();
        }

        var stale = await Assert.ThrowsAsync<CaptureDecodeSupersededException>(() => first);
        Assert.Equal(1, stale.Generation);
        Assert.Equal(2, second.Generation);
        Assert.Same(second, controller.LastSuccessfulResult);
        Assert.Null(controller.ActiveGeneration);
    }

    [Fact]
    public async Task Cancellation_and_failure_preserve_the_last_successful_result()
    {
        var testCancellation = CancellationToken.None;
        using var controller = new CaptureDecodeController();
        var baseline = await controller.RunAsync(CommonRequest(), cancellationToken: testCancellation);

        using (var blockingProgress = new BlockingProgress(testCancellation))
        {
            var cancelled = controller.RunAsync(CommonRequest(), blockingProgress, testCancellation);
            Assert.True(blockingProgress.Entered.Wait(TimeSpan.FromSeconds(10), testCancellation));
            Assert.True(controller.CancelCurrent());
            blockingProgress.Release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        }
        Assert.Same(baseline, controller.LastSuccessfulResult);

        var invalid = CommonRequest() with
        {
            Format = new FormatDecodeRequest("0x86"),
        };
        var error = await Assert.ThrowsAsync<CaptureDecodeConfigurationException>(() =>
            controller.RunAsync(invalid, cancellationToken: testCancellation));

        Assert.Contains("Unsupported Event Buffer Version", error.Message, StringComparison.Ordinal);
        Assert.Same(baseline, controller.LastSuccessfulResult);
        Assert.Null(controller.ActiveGeneration);
    }

    [Fact]
    public async Task Common_and_Desay_are_resolved_through_the_executable_registry()
    {
        var cancellationToken = CancellationToken.None;
        using var controller = new CaptureDecodeController();

        var common = await controller.RunAsync(
            CommonRequest() with
            {
                Format = new FormatDecodeRequest("0x83", "51927"),
            },
            cancellationToken: cancellationToken);
        var commonDecode = Assert.IsType<CommonFormatDecodeResult>(common.Decode);
        Assert.IsType<CommonFormatSelection>(commonDecode.Selection);
        Assert.Equal("51927", common.Capture.RegisterProfile);
        Assert.Equal(3, common.Workspace.Frames.Count);

        var desay = await controller.RunAsync(
            new CaptureDecodeRequest(
                Fixture("desay97-full-reread.nds.txt"),
                new FormatDecodeRequest("0x97", "51927", "benz-palm")),
            cancellationToken: cancellationToken);
        var desayDecode = Assert.IsType<Desay97FormatDecodeResult>(desay.Decode);
        var desaySelection = Assert.IsType<Desay97FormatSelection>(desayDecode.Selection);
        Assert.Equal("Desay 0x97 / Benz Palm", desaySelection.DisplayIdentity);
        Assert.Equal(2, desay.Workspace.Frames.Count);
        Assert.Same(desay, controller.LastSuccessfulResult);
    }

    [Fact]
    public async Task Progress_is_typed_and_register_projection_preserves_raw_provenance()
    {
        var cancellationToken = CancellationToken.None;
        using var controller = new CaptureDecodeController();
        var updates = new List<CaptureDecodeProgress>();

        var result = await controller.RunAsync(
            CommonRequest() with
            {
                Format = new FormatDecodeRequest("0x83", "51927"),
            },
            new InlineProgress<CaptureDecodeProgress>(updates.Add),
            cancellationToken);

        Assert.Equal(
            [
                CaptureDecodePhase.ProbingSource,
                CaptureDecodePhase.HashingSource,
                CaptureDecodePhase.IndexingRecords,
                CaptureDecodePhase.SourceReady,
                CaptureDecodePhase.SelectingFormat,
                CaptureDecodePhase.ProjectingRegisters,
                CaptureDecodePhase.DecodingFrames,
                CaptureDecodePhase.BuildingWorkspace,
                CaptureDecodePhase.Ready,
            ],
            updates.Select(update => update.Phase).Distinct());
        Assert.All(updates, update => Assert.Equal(result.Generation, update.Generation));
        Assert.Equal(100, updates[^1].Percent);

        Assert.NotSame(result.LoadedCapture, result.Capture);
        Assert.Same(result.LoadedCapture.Records, result.Capture.Records);
        for (var index = 0; index < result.LoadedCapture.Records.Count; index++)
        {
            var loaded = result.LoadedCapture.Records[index];
            var projected = result.Capture.Records[index];
            Assert.Same(loaded, projected);
            Assert.Same(loaded.Data, projected.Data);
            Assert.Same(loaded.SourceFields, projected.SourceFields);
            Assert.Equal(loaded.RawText, projected.RawText);
            Assert.Equal(loaded.StableId, projected.StableId);
            Assert.Equal(loaded.Location, projected.Location);
        }
    }

    private static CaptureDecodeRequest CommonRequest() => new(
        Fixture("common-0x83-asil-lifecycle.nds.txt"),
        new FormatDecodeRequest("0x83"));

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..",
        "fixtures",
        name));

    private sealed class BlockingProgress(
        CancellationToken cancellationToken,
        CaptureDecodePhase blockingPhase = CaptureDecodePhase.ProbingSource)
        : IProgress<CaptureDecodeProgress>, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(false);
        public ManualResetEventSlim Release { get; } = new(false);

        public void Report(CaptureDecodeProgress value)
        {
            if (value.Phase != blockingPhase) return;
            Entered.Set();
            Release.Wait(cancellationToken);
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
