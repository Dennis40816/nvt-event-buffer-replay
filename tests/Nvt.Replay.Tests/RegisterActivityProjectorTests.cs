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

    private static SourceRecord Record(long index, BusOperation operation, uint address, params byte[] data) =>
        new(index, $"source-{index}", null, operation, "TP", address, data.Length, data, "raw", new SourceLocation(index, (int)index));
}
