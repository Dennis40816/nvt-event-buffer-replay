// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;

namespace Nvt.Replay.Tests;

public sealed class CaptureWorkspaceViewModelTests
{
    [Fact]
    public void NewWorkspaceIsEmpty()
    {
        var workspace = new CaptureWorkspaceViewModel();

        Assert.IsType<CaptureState.Empty>(workspace.State);
        Assert.Null(workspace.Capture);
        Assert.Null(workspace.Replay);
    }

    [Fact]
    public async Task LoadAdoptsProbedCapture()
    {
        var workspace = new CaptureWorkspaceViewModel();

        var capture = await CaptureTestSupport.ProbeAsync(workspace);

        Assert.Same(capture, Assert.IsType<CaptureState.Probed>(workspace.State).Capture);
        Assert.Equal(capture.Session.Records.Count, capture.Raw.Rows.Count);
        Assert.Null(workspace.Replay);
    }

    [Fact]
    public async Task DecodeAdoptsCompleteReplay()
    {
        var workspace = new CaptureWorkspaceViewModel();

        var replay = await CaptureTestSupport.DecodeAsync(workspace);

        Assert.Same(replay, Assert.IsType<CaptureState.Decoded>(workspace.State).Replay);
        Assert.Same(replay.Decoded.Capture, workspace.Capture!.Session);
        Assert.Equal(replay.Frames.Count, replay.Rows.Count);
    }

    [Fact]
    public async Task PendingLoadCoexistsWithDisplayedReplay()
    {
        var workspace = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.DecodeAsync(workspace);
        var displayed = workspace.State;

        var pending = workspace.BeginLoad();

        Assert.Same(displayed, workspace.State);
        Assert.Same(pending, workspace.Pending);
    }

    [Fact]
    public async Task FailedLoadKeepsDisplayedReplay()
    {
        var workspace = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.DecodeAsync(workspace);
        var displayed = workspace.State;
        var pending = workspace.BeginLoad();

        Assert.True(workspace.Fail(pending));

        Assert.Same(displayed, workspace.State);
        Assert.Null(workspace.Pending);
    }

    [Fact]
    public async Task FailedDecodeKeepsDisplayedReplay()
    {
        var workspace = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.DecodeAsync(workspace);
        var displayed = workspace.State;
        using var controller = new CaptureDecodeController();
        var operation = CaptureTestSupport.StartDecode(controller, workspace.Capture!);
        var pending = workspace.BeginDecode(operation);
        await operation.Completion;

        workspace.Fail(pending);

        Assert.Same(displayed, workspace.State);
    }

    [Fact]
    public async Task ClearRemovesCaptureAndReplay()
    {
        var workspace = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.DecodeAsync(workspace);

        workspace.Clear();

        Assert.IsType<CaptureState.Empty>(workspace.State);
        Assert.Null(workspace.Capture);
        Assert.Null(workspace.Replay);
    }

    [Fact]
    public async Task LateDecodeAfterClearIsDropped()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        using var controller = new CaptureDecodeController();
        var operation = CaptureTestSupport.StartDecode(controller, capture);
        var pending = workspace.BeginDecode(operation);
        workspace.Clear();
        var replay = DecodedCaptureProjection.Create(await operation.Completion, CancellationToken.None);

        Assert.False(workspace.AdoptDecoded(pending, replay));

        Assert.IsType<CaptureState.Empty>(workspace.State);
    }

    [Fact]
    public async Task ClearLeavesOperationCompletionWithItsExistingOwner()
    {
        var workspace = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.ProbeAsync(workspace);
        var pending = workspace.BeginLoad();

        workspace.Clear();

        Assert.Same(pending, workspace.Pending);
        Assert.True(workspace.Fail(pending));
    }

    [Fact]
    public async Task ReplacementSourceDropsPreviousDecode()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        using var controller = new CaptureDecodeController();
        var operation = CaptureTestSupport.StartDecode(controller, capture);
        var pending = workspace.BeginDecode(operation);
        var replacement = await CaptureTestSupport.ProbeAsync(workspace);
        var replay = DecodedCaptureProjection.Create(await operation.Completion, CancellationToken.None);

        Assert.False(workspace.AdoptDecoded(pending, replay));

        Assert.Same(replacement, workspace.Capture);
    }

    [Fact]
    public async Task SupersededLoadCannotAdopt()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        var first = workspace.BeginLoad();
        var second = workspace.BeginLoad();

        Assert.False(workspace.AdoptProbed(first, capture));

        Assert.Same(second, workspace.Pending);
    }

    [Fact]
    public void SupersededFailureCannotReleaseCurrentOperation()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var first = workspace.BeginLoad();
        var second = workspace.BeginLoad();

        Assert.False(workspace.Fail(first));

        Assert.Same(second, workspace.Pending);
    }

    [Fact]
    public async Task DecodeRequiresLoadedCapture()
    {
        var sourceWorkspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(sourceWorkspace);
        using var controller = new CaptureDecodeController();
        var operation = CaptureTestSupport.StartDecode(controller, capture);
        await operation.Completion;
        var empty = new CaptureWorkspaceViewModel();

        Assert.Throws<InvalidOperationException>(() => empty.BeginDecode(operation));
    }

    [Fact]
    public async Task DecodeWithDifferentGenerationCannotAdopt()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        using var controller = new CaptureDecodeController();
        var operation = CaptureTestSupport.StartDecode(controller, capture);
        var pending = workspace.BeginDecode(operation);
        var result = await operation.Completion;
        var replay = DecodedCaptureProjection.Create(result with { Generation = result.Generation + 1 }, CancellationToken.None);

        Assert.False(workspace.AdoptDecoded(pending, replay));

        Assert.IsType<CaptureState.Probed>(workspace.State);
    }

    [Fact]
    public async Task DecodeWithDifferentInputCannotAdopt()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        using var controller = new CaptureDecodeController();
        var operation = CaptureTestSupport.StartDecode(controller, capture);
        var pending = workspace.BeginDecode(operation);
        var other = await CaptureTestSupport.ProbeAsync(new CaptureWorkspaceViewModel());
        var result = await operation.Completion;
        var replay = DecodedCaptureProjection.Create(result with { LoadedCapture = other.Session }, CancellationToken.None);

        Assert.False(workspace.AdoptDecoded(pending, replay));
    }

    [Fact]
    public async Task ChangedSourceDropsDecodeWithoutReplacingPendingToken()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        using var controller = new CaptureDecodeController();
        var operation = CaptureTestSupport.StartDecode(controller, capture);
        var pending = workspace.BeginDecode(operation);
        workspace.UpdateProfile(capture with { Session = capture.Session.WithRegisterProfile("51927") });
        var replay = DecodedCaptureProjection.Create(await operation.Completion, CancellationToken.None);

        Assert.False(workspace.AdoptDecoded(pending, replay));
    }

    [Fact]
    public void SourceChoiceRetainsPathAndConfigurationIntent()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var pending = workspace.BeginLoad();
        var source = new PendingCaptureSource("capture.csv", true);

        Assert.True(workspace.RequireSourceChoice(pending, source));

        Assert.Same(source, workspace.PendingSource);
    }

    [Fact]
    public void SupersededLoadCannotReplaceSourceChoice()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var first = workspace.BeginLoad();
        workspace.BeginLoad();

        Assert.False(workspace.RequireSourceChoice(first, new PendingCaptureSource("old.csv", false)));

        Assert.Null(workspace.PendingSource);
    }

    [Fact]
    public async Task SuccessfulReplacementClearsSourceChoice()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var pending = workspace.BeginLoad();
        workspace.RequireSourceChoice(pending, new PendingCaptureSource("old.csv", true));

        await CaptureTestSupport.ProbeAsync(workspace);

        Assert.Null(workspace.PendingSource);
    }

    [Fact]
    public void ClearRemovesSourceChoice()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var pending = workspace.BeginLoad();
        workspace.RequireSourceChoice(pending, new PendingCaptureSource("capture.csv", true));

        workspace.Clear();

        Assert.Null(workspace.PendingSource);
    }

    [Fact]
    public void ProfileReplacementRequiresCapture()
    {
        var empty = new CaptureWorkspaceViewModel();

        Assert.Throws<InvalidOperationException>(() => empty.UpdateProfile(null!));
    }

    [Fact]
    public void ReviewReplacementRequiresCapture()
    {
        var empty = new CaptureWorkspaceViewModel();

        Assert.Throws<InvalidOperationException>(() => empty.SetReviewWorkspace(new ReviewInspectorWorkspace([], [])));
    }

    [Fact]
    public async Task DiagnosticLinesComeFromTheOwnedProjection()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);

        Assert.Equal(capture.Diagnostics.Rows.Select(row => row.Diagnostic.Location.LineNumber).OrderBy(line => line),
            capture.Diagnostics.LineNumbers);
    }

    [Fact]
    public async Task LogicalLookupFindsSourceInPhysicalRecords()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var replay = await CaptureTestSupport.DecodeAsync(workspace);
        var source = replay.Rows[0].PhysicalRecords[0];

        Assert.Equal(0, workspace.FindLogicalIndex(source.StableId));
    }

    [Fact]
    public async Task LogicalLookupRejectsMissingSource()
    {
        var workspace = new CaptureWorkspaceViewModel();
        await CaptureTestSupport.DecodeAsync(workspace);

        Assert.Equal(-1, workspace.FindLogicalIndex("missing-source"));
    }

    [Fact]
    public void LogicalLookupHasNoReplayInEmptyPhase()
    {
        var workspace = new CaptureWorkspaceViewModel();

        Assert.Equal(-1, workspace.FindLogicalIndex("missing-source"));
    }
    [Fact]
    public async Task ConfigurationCancelKeepsProbedCapture()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        workspace.Configure(Nvt.Replay.Sources.NvtRegisterProfileInference.Infer(capture.Session.Records, 0x01));
        var displayed = workspace.State;

        workspace.DeferConfiguration();

        Assert.Same(displayed, workspace.State);
        Assert.Null(workspace.PendingConfiguration);
        Assert.Null(workspace.Replay);
    }

    [Fact]
    public async Task ConfigurationRequiresCapture()
    {
        var source = await CaptureTestSupport.ProbeAsync(new CaptureWorkspaceViewModel());
        var inference = Nvt.Replay.Sources.NvtRegisterProfileInference.Infer(source.Session.Records, 0x01);
        var empty = new CaptureWorkspaceViewModel();

        Assert.Throws<InvalidOperationException>(() => empty.Configure(inference));
    }

    [Fact]
    public async Task ProfileReplacementRetainsExistingReplay()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var replay = await CaptureTestSupport.DecodeAsync(workspace);
        var capture = workspace.Capture!;

        workspace.UpdateProfile(capture with { Session = capture.Session.WithRegisterProfile("51927") });

        Assert.Same(replay, workspace.Replay);
        Assert.Equal("51927", workspace.Capture!.Session.RegisterProfile);
    }

    [Fact]
    public async Task PublishedRawRowsRejectCollectionMutation()
    {
        var workspace = new CaptureWorkspaceViewModel();
        var capture = await CaptureTestSupport.ProbeAsync(workspace);
        var rows = Assert.IsAssignableFrom<IList<RawRecordRow>>(capture.Raw.Rows);

        Assert.Throws<NotSupportedException>(() => rows.Clear());

        Assert.Equal(capture.Session.Records.Count, capture.Raw.Rows.Count);
    }

}
