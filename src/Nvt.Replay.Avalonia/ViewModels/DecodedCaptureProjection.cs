// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Collections.ObjectModel;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Avalonia.ViewModels;

internal sealed record DecodedCaptureProjection
{
    private DecodedCaptureProjection(
        CaptureDecodeResult result,
        DecodedFrameRow[] rows,
        CaptureDiagnostics diagnostics,
        ReplayTrailHistory history)
    {
        Decoded = result;
        Rows = Array.AsReadOnly(rows);
        RowsBySourceId = new ReadOnlyDictionary<string, DecodedFrameRow>(rows
            .SelectMany(row => row.PhysicalRecords.Append(row.Source).Select(source => (source.StableId, Row: row)))
            .GroupBy(item => item.StableId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Row, StringComparer.Ordinal));
        Diagnostics = diagnostics;
        TrailHistory = history;
    }

    internal CaptureDecodeResult Decoded { get; }
    internal ITouchReplaySession Session => Decoded.Decode.Replay;
    internal ReplayFrameCache Frames => Decoded.Workspace.Frames;
    internal ReplayDecodeConfiguration Configuration => Decoded.Decode.Configuration;
    internal IReadOnlyList<DecodedFrameRow> Rows { get; }
    internal IReadOnlyDictionary<string, DecodedFrameRow> RowsBySourceId { get; }
    internal CaptureDiagnostics Diagnostics { get; }
    internal ReplayTrailHistory TrailHistory { get; }
    internal ReplayExtent Extent => new(Decoded.Workspace.Extent.MaximumX, Decoded.Workspace.Extent.MaximumY);

    internal static DecodedCaptureProjection Create(CaptureDecodeResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rows = result.Decode switch
        {
            CommonFormatDecodeResult common => common.Report.Frames
                .Select((frame, index) => DecodedFrameRow.FromCommon(index, frame)).ToArray(),
            Desay97FormatDecodeResult desay => desay.Report.Frames
                .Select((frame, index) => DecodedFrameRow.FromDesay97(index, frame)).ToArray(),
            _ => throw new InvalidDataException($"Unsupported executable format result '{result.Decode.GetType().Name}'."),
        };
        var diagnostics = new CaptureDiagnostics(result.Decode.Diagnostics);
        var history = ReplayTrailHistory.Create(result.Workspace.Frames.Snapshots, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new DecodedCaptureProjection(result, rows, diagnostics, history);
    }
}
