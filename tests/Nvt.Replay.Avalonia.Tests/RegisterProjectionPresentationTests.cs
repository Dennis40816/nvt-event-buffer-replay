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

    [Fact]
    public void Raw_row_shows_projected_register_and_captured_bus_addresses_on_separate_lines()
    {
        var sourceFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adapter"] = "kingstvis",
            ["raw_address"] = "0x03",
            ["register_offset"] = "0x00",
            ["register_name"] = "event_buffer",
            ["register_page_known"] = "false",
        };
        var record = new SourceRecord(
            0,
            "capture:L1",
            null,
            BusOperation.Read,
            "TP",
            null,
            80,
            Enumerable.Repeat((byte)0xFF, 80).ToArray(),
            "raw",
            new SourceLocation(0, 1),
            new I2cTransport(1, [[0x00]], [], null),
            sourceFields);
        var index = new RegisterAnnotationIndex([record]);
        var projection = index.Project(
            NvtRegisterCatalog.FindProfile("51927"),
            TestContext.Current.CancellationToken);
        var activity = Assert.Single(RegisterActivityProjector.Project([record], projection));

        var row = new RawRecordRow(record, activity, projection.Find(record.StableId));

        Assert.Equal("REG 0x99000", row.AddressPrimary);
        Assert.Equal("I2C 0x03 R", row.AddressSecondary);
        Assert.Contains("inferred", row.AddressTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.True(row.Matches("0x99000"));
        Assert.True(row.Matches("0x03"));
        Assert.Null(record.Address);
        Assert.Same(sourceFields, record.SourceFields);
    }

    [Fact]
    public void Raw_row_names_switch_page_and_keeps_the_bus_address_visible()
    {
        var bytes = new byte[] { 0xFF, 0x08, 0x08, 0x00 };
        var record = new SourceRecord(
            0,
            "capture:L1",
            null,
            BusOperation.Write,
            "TP",
            null,
            bytes.Length,
            bytes,
            "raw",
            new SourceLocation(0, 1),
            new I2cTransport(1, [], [], null),
            new Dictionary<string, string> { ["raw_address"] = "0x02" });
        var activity = Assert.Single(RegisterActivityProjector.Project([record]));

        var row = new RawRecordRow(record, activity);

        Assert.Equal("PAGE 0x80800", row.AddressPrimary);
        Assert.Equal("I2C 0x02 W", row.AddressSecondary);
        Assert.Equal("Switch page · 0x80800", row.Register);
        Assert.Contains("Switch page", row.AddressTooltip, StringComparison.Ordinal);
        Assert.Equal(bytes, record.Data);
        Assert.Null(record.Address);
    }
}
