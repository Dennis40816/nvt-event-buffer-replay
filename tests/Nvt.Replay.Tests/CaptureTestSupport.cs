// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

internal static class CaptureTestSupport
{
    internal static async Task<CaptureData> ProbeAsync(CaptureWorkspaceViewModel workspace)
    {
        var pending = workspace.BeginLoad();
        var source = await CaptureSession.LoadAsync(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "kingstvis-common-0x83.csv")));
        var data = new CaptureData(source,
            RawCaptureProjection.Create(source, RegisterActivityProjector.Project(source.Records, source.RegisterAnnotations)),
            new CaptureDiagnostics(source.TransportDiagnostics.Concat(source.RegisterDiagnostics)));
        workspace.AdoptProbed(pending, data);
        workspace.Fail(pending);
        return data;
    }

    internal static CaptureDecodeOperation StartDecode(CaptureDecodeController controller, CaptureData capture) =>
        controller.StartPrepared(new PreparedCaptureDecodeRequest(capture.Session, new FormatDecodeRequest("0x83")));

    internal static async Task<DecodedCaptureProjection> DecodeAsync(CaptureWorkspaceViewModel workspace)
    {
        var capture = await ProbeAsync(workspace);
        using var controller = new CaptureDecodeController();
        var operation = StartDecode(controller, capture);
        var pending = workspace.BeginDecode(operation);
        var projection = DecodedCaptureProjection.Create(await operation.Completion, CancellationToken.None);
        workspace.AdoptDecoded(pending, projection);
        workspace.Fail(pending);
        return projection;
    }

    internal static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
