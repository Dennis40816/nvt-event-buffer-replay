using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class RegisterActivityProjectorTests
{
    [Fact]
    public void Projection_classifies_commands_resets_and_only_changes_after_a_prior_read()
    {
        var records = new[]
        {
            Record(0, BusOperation.Read, 0x99070, 0x01),
            Record(1, BusOperation.Read, 0x99070, 0x01),
            Record(2, BusOperation.Read, 0x99070, 0x02),
            Record(3, BusOperation.Write, 0x99050, 0x23),
            Record(4, BusOperation.Write, 0xFF0FE, 0x69),
        }.Select(record => NvtRegisterCatalog.Reannotate(record, "51927")).ToArray();

        var activities = RegisterActivityProjector.Project(records);

        Assert.Equal(RegisterActivityKind.Read, activities[0].Kind);
        Assert.False(activities[0].HasPreviousSample);
        Assert.False(activities[1].ChangedFromPreviousSample);
        Assert.Equal(RegisterActivityKind.ChangedRead, activities[2].Kind);
        Assert.Equal(RegisterActivityKind.Command, activities[3].Kind);
        Assert.Equal(RegisterActivityKind.Reset, activities[4].Kind);
    }

    [Fact]
    public void Typed_projection_changes_profile_semantics_without_reannotating_records()
    {
        var record = Record(0, BusOperation.Read, 0x99000, 0x00);
        var records = new[] { record };
        var index = new RegisterAnnotationIndex(records);
        var wrongProfile = index.Project(NvtRegisterCatalog.FindProfile("51929/51932"));
        var profile51927 = index.Project(NvtRegisterCatalog.FindProfile("51927"));

        Assert.Empty(RegisterActivityProjector.Project(records, wrongProfile));
        var activity = Assert.Single(RegisterActivityProjector.Project(records, profile51927));

        Assert.Equal("51927", activity.Profile);
        Assert.Equal("Event Buffer", activity.Region);
        Assert.Equal("Event Buffer · 00", activity.Register);
        Assert.Same(record, records[0]);
        Assert.Null(record.SourceFields);
    }

    [Fact]
    public void Confirmed_profile_projects_offset_only_event_buffer_reads_without_changing_raw_source()
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
            "source-0",
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
        var records = new[] { record };
        var index = new RegisterAnnotationIndex(records);

        Assert.Empty(RegisterActivityProjector.Project(records, index.Project(null)));
        var projection = index.Project(NvtRegisterCatalog.FindProfile("51927"));
        var activity = Assert.Single(RegisterActivityProjector.Project(records, projection));
        var annotation = Assert.IsType<RegisterAnnotation>(projection.Find(record.StableId));

        Assert.Equal(0x99000u, annotation.Description.Address);
        Assert.Equal("inferred", annotation.Description.ProfileResolution);
        Assert.Equal("Event Buffer", activity.Region);
        Assert.True(activity.IsKnown);
        Assert.Null(record.Address);
        Assert.Same(sourceFields, record.SourceFields);
        Assert.Equal("false", record.SourceFields?["register_page_known"]);
    }

    [Fact]
    public void Page_switch_is_a_first_class_register_activity()
    {
        var command = Record(0, BusOperation.Write, address: 0, 0xFF, 0x08, 0x08, 0x00) with { Address = null };

        var activity = Assert.Single(RegisterActivityProjector.Project([command]));

        Assert.Equal(RegisterActivityKind.PageSwitch, activity.Kind);
        Assert.Equal("Switch page · 0x80800", activity.Register);
        Assert.Equal("Page select", activity.Meaning);
        Assert.True(activity.IsKnown);
        Assert.Equal(new byte[] { 0xFF, 0x08, 0x08, 0x00 }, command.Data);
        Assert.Null(command.Address);
    }

    private static SourceRecord Record(long index, BusOperation operation, uint address, params byte[] data) =>
        new(index, $"source-{index}", null, operation, "TP", address, data.Length, data, "raw", new SourceLocation(index, (int)index));
}
