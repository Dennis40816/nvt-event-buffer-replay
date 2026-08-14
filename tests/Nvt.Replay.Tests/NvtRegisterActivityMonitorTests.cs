using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Tests;

public sealed class NvtRegisterActivityMonitorTests
{
    [Fact]
    public void Frame_counter_samples_group_as_raw_device_state_without_endian_guess()
    {
        var diagnostics = new NvtRegisterActivityMonitor().Observe(
        [
            Record(1, 0x99070, 0x12, 0x34),
            Record(2, 0x99070, 0x12, 0x35),
            Record(3, 0x99070, 0x12, 0x35),
        ]);

        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, item => Assert.Equal("NVT_FRAME_COUNTER_SAMPLE", item.Code));
        Assert.Equal(["True", "True", "False"], diagnostics.Select(item => item.Details?["changed_from_previous_sample"]));
        Assert.All(diagnostics, item => Assert.Equal("raw_only", item.Details?["meaning"]));
        Assert.Equal(3, Assert.Single(new ReviewSession(diagnostics).Groups).Occurrences.Count);
    }

    [Fact]
    public void Known_registers_show_raw_values_and_only_confirmed_fw_state_meaning()
    {
        var diagnostics = new NvtRegisterActivityMonitor().Observe(
        [
            Record(1, 0x99060, 0xA3),
            Record(2, 0x99060, 0xA2),
            Record(3, 0x99076, 0x01, 0x02),
            Record(4, 0x99078, 0x03, 0x04),
        ]);

        Assert.Contains(diagnostics, item => item.Code == "NVT_FW_STATE_SAMPLE" && item.Details?["meaning"] == "normal_run");
        Assert.Contains(diagnostics, item => item.Code == "NVT_FW_STATE_SAMPLE" && item.Details?["raw"] == "A2" && item.Details?["meaning"] == "raw_only");
        Assert.Contains(diagnostics, item => item.Code == "NVT_DP_VERSION_SAMPLE" && item.Details?["raw"] == "01 02");
        Assert.Contains(diagnostics, item => item.Code == "NVT_TP_FW_VERSION_SAMPLE" && item.Details?["raw"] == "03 04");
    }

    private static SourceRecord Record(long index, uint address, params byte[] data) =>
        new(index, $"source-{index}", null, BusOperation.Read, "TP", address, data.Length, data, "raw", new SourceLocation(index, (int)index));
}
