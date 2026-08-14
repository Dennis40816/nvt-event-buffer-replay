using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Tests;

public sealed class CommonStreamMonitorTests
{
    [Fact]
    public void Asil_alarm_is_edge_triggered_and_clear_is_reported()
    {
        var monitor = new CommonStreamMonitor();
        var alarm = Frame(CommonEventBufferVersion.V83, packet => packet[73] = 0x1A, 1);
        var repeated = Frame(CommonEventBufferVersion.V83, packet => packet[73] = 0x1A, 2);
        var clear = Frame(CommonEventBufferVersion.V83, _ => { }, 3);

        var diagnostics = monitor.Observe(alarm)
            .Concat(monitor.Observe(repeated))
            .Concat(monitor.Observe(clear))
            .ToArray();

        Assert.Equal(["COMMON_ASIL_ALARM", "COMMON_ASIL_CLEARED"], diagnostics.Select(item => item.Code));
    }

    [Fact]
    public void Bus_counter_warns_only_when_it_increases()
    {
        var monitor = new CommonStreamMonitor();
        var frames = new[]
        {
            Frame(CommonEventBufferVersion.V84, packet => packet[78] = 0xE0, 1),
            Frame(CommonEventBufferVersion.V84, packet => packet[78] = 0xE0, 2),
            Frame(CommonEventBufferVersion.V84, packet => packet[78] = 0xF1, 3),
            Frame(CommonEventBufferVersion.V84, packet => packet[78] = 0xF1, 4),
        };

        var diagnostics = frames.SelectMany(monitor.Observe)
            .Where(item => item.Code == "COMMON_BUS_COUNTER_INCREASED")
            .ToArray();

        Assert.Equal(2, diagnostics.Length);
        Assert.Equal(["no_response", "busy"], diagnostics.Select(item => item.Details?["counter"]));
    }

    [Fact]
    public void Global_palm_accepts_all_break_and_warns_for_touch_payload()
    {
        var monitor = new CommonStreamMonitor();
        var valid = Frame(CommonEventBufferVersion.V85, packet => packet[75] = 0x01, 1);
        var invalid = Frame(
            CommonEventBufferVersion.V85,
            packet =>
            {
                packet[0] = 1;
                packet[1] = 0x12;
                packet[2] = 0;
                packet[3] = 1;
                packet[4] = 0;
                packet[5] = 2;
                packet[75] = 0x01;
            },
            2);

        Assert.DoesNotContain(monitor.Observe(valid), item => item.Code == "COMMON_GLOBAL_PALM_INCONSISTENT");
        Assert.Contains(monitor.Observe(invalid), item => item.Code == "COMMON_GLOBAL_PALM_INCONSISTENT");
    }

    [Fact]
    public void Zero_touches_with_one_real_break_is_not_an_all_break_mismatch()
    {
        var monitor = new CommonStreamMonitor();
        var breakFrame = Frame(
            CommonEventBufferVersion.V83,
            packet =>
            {
                packet[1] = 0x13;
                packet[2] = 0;
                packet[3] = 10;
                packet[4] = 0;
                packet[5] = 20;
            },
            1);

        Assert.DoesNotContain(
            monitor.Observe(breakFrame),
            item => item.Code == "COMMON_ALL_BREAK_PAYLOAD_MISMATCH");
    }

    private static CommonEventBufferFrame Frame(
        CommonEventBufferVersion version,
        Action<byte[]> mutate,
        long index)
    {
        var packet = CommonEventBufferDecoderTests.NewAllBreak(version);
        mutate(packet);
        CommonEventBufferDecoderTests.Seal(packet);
        return Assert.IsType<CommonEventBufferFrame>(
            new CommonEventBufferDecoder().Decode(CommonEventBufferDecoderTests.Source(packet, index), version).Frame);
    }
}
