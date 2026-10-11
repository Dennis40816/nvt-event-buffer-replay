// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Collections.ObjectModel;
using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Avalonia.ViewModels;

internal abstract record CaptureState
{
    private CaptureState() { }

    internal sealed record Empty : CaptureState;

    internal sealed record Probed(CaptureData Capture) : CaptureState;

    internal sealed record Decoded(CaptureData Capture, DecodedCaptureProjection Replay) : CaptureState;
}

internal sealed record CaptureData(
    CaptureSession Session,
    RawCaptureProjection Raw,
    CaptureDiagnostics Diagnostics,
    ReviewInspectorWorkspace? Review = null);

internal sealed record RawCaptureProjection
{
    private RawCaptureProjection(
        RawRecordRow[] rows,
        RegisterActivityEntry[] activities)
    {
        Rows = Array.AsReadOnly(rows);
        Activities = Array.AsReadOnly(activities);
        RowsById = new ReadOnlyDictionary<string, RawRecordRow>(
            rows.ToDictionary(row => row.Record.StableId, StringComparer.Ordinal));
    }

    internal IReadOnlyList<RawRecordRow> Rows { get; }
    internal IReadOnlyList<RegisterActivityEntry> Activities { get; }
    internal IReadOnlyDictionary<string, RawRecordRow> RowsById { get; }

    internal static RawCaptureProjection Create(
        CaptureSession session,
        IEnumerable<RegisterActivityEntry> activities)
    {
        var ownedActivities = activities.ToArray();
        var byId = ownedActivities.ToDictionary(item => item.Record.StableId, StringComparer.Ordinal);
        var rows = session.Records.Select(record => new RawRecordRow(
            record,
            byId.GetValueOrDefault(record.StableId),
            session.RegisterAnnotations.Find(record.StableId))).ToArray();
        return new RawCaptureProjection(rows, ownedActivities);
    }
}

internal sealed record CaptureDiagnostics
{
    internal CaptureDiagnostics(IEnumerable<ReplayDiagnostic> diagnostics)
    {
        var rows = diagnostics.Select(diagnostic => new DiagnosticRow(diagnostic)).ToArray();
        Rows = Array.AsReadOnly(rows);
        LineNumbers = Array.AsReadOnly(rows.Select(row => row.Diagnostic.Location.LineNumber)
            .OrderBy(line => line).ToArray());
    }

    internal IReadOnlyList<DiagnosticRow> Rows { get; }
    internal IReadOnlyList<int> LineNumbers { get; }
}

internal abstract record PendingCaptureOperation
{
    private PendingCaptureOperation() { }

    // The instance is the load token, so equality is by reference. Decode tokens come from the existing controller.
    internal sealed record Load : PendingCaptureOperation
    {
        public bool Equals(Load? other) => ReferenceEquals(this, other);

        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    internal sealed record Decode(CaptureSession Source, CaptureDecodeOperation Operation) : PendingCaptureOperation;
}

internal sealed record PendingCaptureSource(string Path, bool RequiresConfiguration);
