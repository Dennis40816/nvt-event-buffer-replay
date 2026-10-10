// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Analysis;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Avalonia.ViewModels;

internal sealed class CaptureWorkspaceViewModel
{
    // UI thread only. Headless consumers serialize calls on their owning thread.
    private CaptureState _state = new CaptureState.Empty();
    // UI thread only. This token does not own cancellation or the shared busy state.
    private PendingCaptureOperation? _pending;
    // UI thread only. A tied source probe awaits an explicit adapter choice.
    private PendingCaptureSource? _pendingSource;

    // UI thread only. The setup overlay projects this pending configuration.
    private NvtRegisterProfileInferenceResult? _configuration;

    internal NvtRegisterProfileInferenceResult? PendingConfiguration => _configuration;
    internal CaptureState State => _state;
    internal PendingCaptureOperation? Pending => _pending;
    internal PendingCaptureSource? PendingSource => _pendingSource;
    internal CaptureData? Capture => _state switch
    {
        CaptureState.Probed probed => probed.Capture,
        CaptureState.Decoded decoded => decoded.Capture,
        _ => null,
    };
    internal DecodedCaptureProjection? Replay => (_state as CaptureState.Decoded)?.Replay;

    internal int FindLogicalIndex(string sourceId)
    {
        var rows = Replay?.Rows;
        if (rows is null) return -1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].PhysicalRecords.Any(source => source.StableId == sourceId)) return index;
        }
        return -1;
    }

    internal PendingCaptureOperation.Load BeginLoad()
    {
        var pending = new PendingCaptureOperation.Load();
        _pending = pending;
        return pending;
    }

    internal PendingCaptureOperation.Decode BeginDecode(CaptureDecodeOperation operation)
    {
        var source = Capture?.Session ?? throw new InvalidOperationException("A loaded capture is required.");
        var pending = new PendingCaptureOperation.Decode(source, operation);
        _pending = pending;
        return pending;
    }

    internal bool IsCurrent(PendingCaptureOperation pending) => ReferenceEquals(_pending, pending);

    internal bool AdoptProbed(PendingCaptureOperation.Load pending, CaptureData capture)
    {
        if (!IsCurrent(pending)) return false;
        _state = new CaptureState.Probed(capture);
        _pendingSource = null;
        return true;
    }

    internal bool AdoptDecoded(PendingCaptureOperation.Decode pending, DecodedCaptureProjection replay)
    {
        var capture = Capture;
        if (!IsCurrent(pending) || capture is null ||
            !ReferenceEquals(capture.Session, pending.Source) ||
            replay.Decoded.Generation != pending.Operation.Generation ||
            !ReferenceEquals(replay.Decoded.LoadedCapture, pending.Source)) return false;
        _state = new CaptureState.Decoded(capture with
        {
            Session = replay.Decoded.Capture,
            Diagnostics = replay.Diagnostics,
        }, replay);
        return true;
    }

    internal bool Fail(PendingCaptureOperation pending)
    {
        if (!IsCurrent(pending)) return false;
        _pending = null;
        return true;
    }

    internal bool RequireSourceChoice(PendingCaptureOperation.Load pending, PendingCaptureSource source)
    {
        if (!IsCurrent(pending)) return false;
        _pendingSource = source;
        return true;
    }

    internal void UpdateProfile(CaptureData capture)
    {
        _state = _state switch
        {
            CaptureState.Probed => new CaptureState.Probed(capture),
            CaptureState.Decoded decoded => decoded with { Capture = capture },
            _ => throw new InvalidOperationException("A loaded capture is required."),
        };
    }

    internal void SetReviewWorkspace(ReviewInspectorWorkspace review)
    {
        var capture = Capture ?? throw new InvalidOperationException("A loaded capture is required.");
        UpdateProfile(capture with { Review = review });
    }

    internal void Configure(NvtRegisterProfileInferenceResult inference)
    {
        if (_state is CaptureState.Empty) throw new InvalidOperationException("A loaded capture is required.");
        _configuration = inference;
    }

    internal void DeferConfiguration() => _configuration = null;

    internal void Clear()
    {
        // Clearing the view does not own operation cancellation or completion.
        _state = new CaptureState.Empty();
        _pendingSource = null;
        _configuration = null;
    }
}
