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

    private static SourceRecord Record(long index, BusOperation operation, uint address, params byte[] data) =>
        new(index, $"source-{index}", null, operation, "TP", address, data.Length, data, "raw", new SourceLocation(index, (int)index));
}
