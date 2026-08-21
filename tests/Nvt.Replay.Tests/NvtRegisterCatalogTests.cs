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
        var unknown = NvtRegisterCatalog.Describe(0x99050, BusOperation.Write, [0x5E]);

        Assert.Equal("FW Command", baselineReset?.Name);
        Assert.Equal("Baseline Reset", baselineReset?.Meaning);
        Assert.True(baselineReset?.FwCommand?.Confirmed);
        Assert.Equal("Unknown FW Command 0x5E", unknown?.Meaning);
        Assert.False(unknown?.FwCommand?.Confirmed);
    }

    [Theory]
    [InlineData(0x11, "Enter Deep Sleep Mode")]
    [InlineData(0x22, "Real-time Raw Data After Algorithm")]
    [InlineData(0x23, "Baseline Reset")]
    [InlineData(0x41, "MP Test: CC Value")]
    [InlineData(0x4C, "MP Test: Doze CC Value")]
    [InlineData(0xD3, "Auto Engineering: DP ASIL Report")]
    public void Common_fw_commands_use_the_packed_category_and_command_byte(byte code, string expected)
    {
        var command = NvtFwCommandParser.Parse([code]);

        Assert.NotNull(command);
        Assert.Equal(expected, command.Name);
        Assert.True(command.Confirmed);
    }

    [Theory]
    [InlineData(0xFF00Eu, "Mode FG", "Control")]
    [InlineData(0xFF01Au, "Mode FG2", "Control")]
    [InlineData(0xFF06Au, "Wakeup Source", "Status")]
    [InlineData(0xFF43Au, "TCON Calibration Enable", "TCON")]
    [InlineData(0xFF805u, "DP Error State", "Diagnostics")]
    [InlineData(0xFF926u, "TP Ready Control", "Control")]
    public void Cross_ic_registers_use_names_verified_in_multiple_regtables(
        uint address,
        string expectedName,
        string expectedRegion)
    {
        var description = NvtRegisterCatalog.Describe(address, BusOperation.Read, [0xA5]);

        Assert.NotNull(description);
        Assert.Equal("Common", description.Profiles);
        Assert.Equal(expectedName, description.Name);
        Assert.Equal(expectedRegion, description.Region);
        Assert.Equal("raw_only", description.Meaning);
        Assert.Equal($"{expectedName} · A5", description.Readable);
    }

    [Fact]
    public void Colliding_event_buffer_base_requires_an_explicit_profile()
    {
        var unresolved = NvtRegisterCatalog.Describe(0x80860, BusOperation.Read, [0xA3]);
        var resolved = NvtRegisterCatalog.Describe(0x80860, BusOperation.Read, [0xA3], "51929/51932");

        Assert.Equal("51929/51932 | 51950/51951", unresolved?.Profiles);
        Assert.Equal("ambiguous", unresolved?.ProfileResolution);
        Assert.Equal("Profile Required", unresolved?.Name);
        Assert.Equal("raw_only", unresolved?.Meaning);
        Assert.Equal("51929/51932", resolved?.Profiles);
        Assert.Equal("resolved", resolved?.ProfileResolution);
        Assert.Equal("FW State", resolved?.Name);
        Assert.Equal("Normal Run", resolved?.Meaning);
    }

    [Fact]
    public void Reannotation_replaces_ambiguous_semantics_without_changing_transport_or_bytes()
    {
        var original = new SourceRecord(
            7, "source-7", null, BusOperation.Read, "TP", 0x80860, 1, [0xA3], "raw",
            new SourceLocation(20, 3));
        var ambiguous = NvtRegisterCatalog.Annotate(original);

        var resolved = NvtRegisterCatalog.Reannotate(ambiguous, "51950/51951");

        Assert.Equal("ambiguous", ambiguous.SourceFields?["register_profile_resolution"]);
        Assert.Equal("51950/51951", resolved.SourceFields?["register_profile"]);
        Assert.Equal("resolved", resolved.SourceFields?["register_profile_resolution"]);
        Assert.Equal("FW State · Normal Run", resolved.SourceFields?["register_readable"]);
        Assert.Same(original.Data, resolved.Data);
        Assert.Equal(original.RawText, resolved.RawText);
    }

    [Fact]
    public void Reannotation_clears_semantics_that_do_not_belong_to_the_selected_profile()
    {
        var original = new SourceRecord(
            8, "source-8", null, BusOperation.Read, "TP", 0x99000, 1, [0x11], "raw",
            new SourceLocation(21, 3));
        var catalogResolved = NvtRegisterCatalog.Annotate(original);

        var wrongProfile = NvtRegisterCatalog.Reannotate(catalogResolved, "51929/51932");

        Assert.Equal("event_buffer", catalogResolved.SourceFields?["register_name"]);
        Assert.False(wrongProfile.SourceFields?.ContainsKey("register_profile"));
        Assert.False(wrongProfile.SourceFields?.ContainsKey("register_region"));
        Assert.False(wrongProfile.SourceFields?.ContainsKey("register_name"));
        Assert.Same(original.Data, wrongProfile.Data);
    }
}
