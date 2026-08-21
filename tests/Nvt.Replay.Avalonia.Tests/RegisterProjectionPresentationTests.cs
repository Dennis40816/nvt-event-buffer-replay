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
    public async Task Nds_paint_row_displays_header_I2C_address_or_absolute_register_by_semantics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nvt-nds-row-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(
                path,
                "2026-08-12 19:21:52:824 Paint TP 0x99000 2 0x50 0x00\n" +
                "2026-08-12 19:21:54:003 Paint TP 0x2A 2 0xA3 0x55",
                TestContext.Current.CancellationToken);
            var records = new List<SourceRecord>();
            await foreach (var record in new NdsCommunicationLogAdapter().ReadAsync(
                new SourceOpenContext(path, "nds"),
                TestContext.Current.CancellationToken))
                records.Add(record);

            var registerRow = new RawRecordRow(records[0]);
            var frameRow = new RawRecordRow(records[1]);
            Assert.Equal("— · R", registerRow.I2cEndpoint);
            Assert.Equal("REG 0x99000", registerRow.AddressPrimary);
            Assert.Equal("RAW ADDR 0x99000", registerRow.AddressSecondary);
            Assert.Equal("0x2A · R", frameRow.I2cEndpoint);
            Assert.Equal("EVENT BUFFER", frameRow.AddressPrimary);
            Assert.Equal("RAW REG —", frameRow.AddressSecondary);
            Assert.Equal("Event Buffer frame", frameRow.Register);
            Assert.Contains("does not report a register", frameRow.AddressTooltip, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

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
        Assert.Equal("— · R", row.I2cEndpoint);
        Assert.Contains("did not report", row.AddressTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Event Buffer", inspector.Subtitle, StringComparison.Ordinal);
        Assert.Contains(inspector.ProtocolRows, item => item.Label == "Profile" && item.Value == "resolved");
        Assert.Same(record, records[0]);
        Assert.Null(record.SourceFields);
    }

    [Fact]
    public void Raw_row_shows_7bit_endpoint_resolved_register_and_raw_register_byte()
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

        Assert.Equal("0x01 · R", row.I2cEndpoint);
        Assert.Equal("REG 0x99000", row.AddressPrimary);
        Assert.Equal("RAW REG 0x00", row.RawRegisterAddress);
        Assert.Equal(row.RawRegisterAddress, row.AddressSecondary);
        Assert.Contains("inferred", row.AddressTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7-bit", row.AddressTooltip, StringComparison.OrdinalIgnoreCase);
        Assert.True(row.Matches("0x99000"));
        Assert.True(row.Matches("0x03"));
        Assert.Null(record.Address);
        Assert.Same(sourceFields, record.SourceFields);
    }

    [Fact]
    public void Raw_row_names_switch_page_and_keeps_7bit_endpoint_and_raw_register_visible()
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

        Assert.Equal("0x01 · W", row.I2cEndpoint);
        Assert.Equal("PAGE 0x80800", row.AddressPrimary);
        Assert.Equal("RAW REG 0xFF", row.RawRegisterAddress);
        Assert.Equal("Switch page · 0x80800", row.Register);
        Assert.Contains("Switch page", row.AddressTooltip, StringComparison.Ordinal);
        Assert.Equal(bytes, record.Data);
        Assert.Null(record.Address);
    }

    [Fact]
    public void Raw_row_builds_full_details_only_when_opened_and_preserves_all_source_evidence()
    {
        var payload = Enumerable.Range(0, 34).Select(value => (byte)value).ToArray();
        var sourceFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["adapter"] = "kingstvis",
            ["dialect"] = "decoded-i2c-v3.5-byte-row",
            ["register_profile"] = "51927",
            ["register_profile_resolution"] = "resolved",
            ["register_address"] = "0x99050",
            ["register_offset"] = "0x50",
            ["register_readable"] = "FW Command · Baseline Reset",
            ["fw_command_code"] = "0x23",
            ["fw_command_name"] = "Baseline Reset",
        };
        var record = new SourceRecord(
            4,
            "capture:L5",
            DateTimeOffset.UnixEpoch,
            BusOperation.Write,
            "TP",
            0x99050,
            34,
            payload,
            "4,0.125,0x01,Write,0x50 0x23",
            new SourceLocation(128, 5),
            new I2cTransport(1, [[0x50]], [true, true], true),
            sourceFields);
        var row = new RawRecordRow(record);

        Assert.False(row.IsExpanded);
        Assert.Null(row.Detail);

        row.SetExpanded(true);

        Assert.True(row.IsExpanded);
        Assert.NotNull(row.Detail);
        Assert.Contains(row.Detail.TransactionFields, field => field.Label == "Address ACK" && field.Value == "ACK");
        Assert.Contains(row.Detail.RegisterFields, field => field.Label == "FW command" && field.Value == "0x23 · Baseline Reset");
        Assert.Contains(row.Detail.SourceFields, field => field.Label == "Dialect" && field.Value.Contains("v3.5", StringComparison.Ordinal));
        Assert.Contains("0000  00 01 02 03", row.Detail.FullPayload, StringComparison.Ordinal);
        Assert.Contains("0020  20 21", row.Detail.FullPayload, StringComparison.Ordinal);
        Assert.Equal(record.RawText, row.Detail.SourceText);
        Assert.Same(payload, record.Data);
        Assert.Same(sourceFields, record.SourceFields);

        row.SetExpanded(false);

        Assert.False(row.IsExpanded);
        Assert.NotNull(row.Detail);
    }
}
