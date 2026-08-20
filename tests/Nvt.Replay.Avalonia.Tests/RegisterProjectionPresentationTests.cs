using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Nvt.Replay.Core;
using Nvt.Replay.Rendering;
using Nvt.Replay.Sources;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class RegisterProjectionPresentationTests
{
    [Fact]
    public void Profile_switch_updates_raw_row_and_inspector_without_copying_the_record()
    {
        var record = new SourceRecord(
            7,
            "capture:L8",
            null,
            BusOperation.Read,
            "TP",
            0x99000,
            1,
            [0x11],
            "raw",
            new SourceLocation(70, 8));
        var records = new[] { record };
        var index = new RegisterAnnotationIndex(records);
        var cancellationToken = TestContext.Current.CancellationToken;
        var wrong = index.Project(NvtRegisterCatalog.FindProfile("51929/51932"), cancellationToken);
        var selected = index.Project(NvtRegisterCatalog.FindProfile("51927"), cancellationToken);

        Assert.Empty(RegisterActivityProjector.Project(records, wrong));
        var activity = Assert.Single(RegisterActivityProjector.Project(records, selected));
        var row = new RawRecordRow(record, activity);
        var annotation = Assert.IsType<RegisterAnnotation>(selected.Find(record.StableId));
        var inspector = InspectorPresentationBuilder.Build(
            record,
            frame: null,
            snapshot: null,
            totalFrames: 0,
            renderMode: ReplayRenderMode.HostState,
            registerAnnotation: annotation);

        Assert.Equal("Event Buffer · 11", row.Register);
        Assert.Contains("Event Buffer", inspector.Subtitle, StringComparison.Ordinal);
        Assert.Contains(inspector.ProtocolRows, item => item.Label == "Profile" && item.Value == "resolved");
        Assert.Same(record, records[0]);
        Assert.Null(record.SourceFields);
    }
}
