// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Rendering;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests.ViewModels;

public sealed class InspectorSelectionTests
{
    [Fact]
    public void SelectionsWithEqualMembersAreEqual()
    {
        var selection = CreateSelection();

        var equivalent = new InspectorSelection(
            selection.Record, selection.Frame, selection.Snapshot, selection.Presentation with { });

        Assert.NotSame(selection, equivalent);
        Assert.Equal(selection, equivalent);
        Assert.Equal(selection.GetHashCode(), equivalent.GetHashCode());
    }

    [Fact]
    public void ReplacingPresentationPreservesSourceEvidenceAndOriginalPresentation()
    {
        var selection = CreateSelection();
        var originalTimestamp = selection.Presentation.TimestampLabel;

        var updated = selection with
        {
            Presentation = selection.Presentation with { TimestampLabel = "00:01.234" },
        };

        Assert.Same(selection.Record, updated.Record);
        Assert.Same(selection.Frame, updated.Frame);
        Assert.Same(selection.Snapshot, updated.Snapshot);
        Assert.Equal(originalTimestamp, selection.Presentation.TimestampLabel);
        Assert.Equal("00:01.234", updated.Presentation.TimestampLabel);
        Assert.Equal(selection.Presentation with { TimestampLabel = "00:01.234" }, updated.Presentation);
        Assert.NotEqual(selection, updated);
    }

    private static InspectorSelection CreateSelection()
    {
        var source = new SourceRecord(0, "synthetic:0", null, BusOperation.Read, "touch", null,
            CommonEventBufferDecoder.FrameLength, new byte[CommonEventBufferDecoder.FrameLength],
            string.Empty, new SourceLocation(0, 1));
        var frame = Assert.IsType<CommonEventBufferFrame>(
            new CommonEventBufferDecoder().Decode(source, CommonEventBufferVersion.V83).Frame);
        var snapshot = new CommonReplaySession([frame]).Seek(0);
        var presentation = InspectorPresentationBuilder.Build(
            source, frame, snapshot, 1, ReplayRenderMode.HostState);
        return new InspectorSelection(source, frame, snapshot, presentation);
    }
}
