using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class NvtRegisterCatalogTests
{
    [Theory]
    [InlineData(0x94000u, "51923", "Event Buffer")]
    [InlineData(0x96A00u, "51926", "Event Buffer")]
    [InlineData(0x8EC98u, "51927", "Common Buffer")]
    [InlineData(0x9D130u, "51929/51932", "History")]
    [InlineData(0xAAD8Cu, "51950/51951", "Common Buffer")]
    public void Known_profile_addresses_have_readable_regions(uint address, string profile, string name)
    {
        var description = NvtRegisterCatalog.Describe(address, BusOperation.Read, [0x11]);

        Assert.NotNull(description);
        Assert.Contains(profile, description.Profiles, StringComparison.Ordinal);
        Assert.Equal(name, description.Name);
        Assert.Equal("raw_only", description.Meaning);
    }

    [Fact]
    public void Confirmed_software_reset_requires_write_value_69()
    {
        var reset = NvtRegisterCatalog.Describe(0xFF0FE, BusOperation.Write, [0x69]);
        var other = NvtRegisterCatalog.Describe(0xFF0FE, BusOperation.Write, [0x68]);

        Assert.Equal("Software Reset", reset?.Meaning);
        Assert.Equal("raw_only", other?.Meaning);
    }

    [Fact]
    public void Fw_command_mailbox_uses_separate_command_parser()
    {
        var baselineReset = NvtRegisterCatalog.Describe(0x99050, BusOperation.Write, [0x23]);
        var unknown = NvtRegisterCatalog.Describe(0x99050, BusOperation.Write, [0x42]);

        Assert.Equal("FW Command", baselineReset?.Name);
        Assert.Equal("Baseline Reset", baselineReset?.Meaning);
        Assert.True(baselineReset?.FwCommand?.Confirmed);
        Assert.Equal("Unknown FW Command 0x42", unknown?.Meaning);
        Assert.False(unknown?.FwCommand?.Confirmed);
    }

    [Fact]
    public void Shared_event_buffer_base_keeps_all_profile_candidates()
    {
        var description = NvtRegisterCatalog.Describe(0x80860, BusOperation.Read, [0xA3]);

        Assert.Equal("51929/51932 | 51950/51951", description?.Profiles);
        Assert.Equal("FW State", description?.Name);
        Assert.Equal("Normal Run", description?.Meaning);
    }
}
