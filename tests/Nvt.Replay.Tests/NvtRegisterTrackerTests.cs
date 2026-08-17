using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class NvtRegisterTrackerTests
{
    [Fact]
    public void Register_write_is_annotated_without_becoming_a_stale_read_pointer()
    {
        var tracker = new NvtRegisterTracker();

        tracker.Observe(Write(0, [0xFF, 0x09, 0x90]));
        var command = tracker.Observe(Write(1, [0x50, 0x23]));
        var unrelatedRead = tracker.Observe(Read(2, [0xA5]));

        Assert.Equal(0x99050u, command.Address);
        Assert.Equal("Baseline Reset", command.SourceFields?["fw_command_name"]);
        Assert.Equal(new byte[] { 0x50, 0x23 }, command.Data);
        Assert.Null(unrelatedRead.Address);
        Assert.Empty(unrelatedRead.I2c?.WriteCommands ?? []);
    }

    [Fact]
    public void New_pointer_replaces_stale_pointer_but_keeps_the_confirmed_page()
    {
        var tracker = new NvtRegisterTracker();

        tracker.Observe(Write(0, [0xFF, 0x09, 0x90]));
        tracker.Observe(Write(1, [0x70]));
        tracker.Observe(Write(2, [0x00]));
        var read = tracker.Observe(Read(3, [0xA5]));

        Assert.Equal(0x99000u, read.Address);
        Assert.Equal(2, read.I2c?.WriteCommands.Count);
        Assert.Equal(new byte[] { 0xFF, 0x09, 0x90 }, read.I2c?.WriteCommands[0]);
        Assert.Equal(new byte[] { 0x00 }, read.I2c?.WriteCommands[1]);
    }

    [Fact]
    public void Software_reset_invalidates_the_remembered_page()
    {
        var tracker = new NvtRegisterTracker();

        tracker.Observe(Write(0, [0xFF, 0x0F, 0xF0]));
        var reset = tracker.Observe(Write(1, [0xFE, 0x69]));
        tracker.Observe(Write(2, [0x00]));
        var read = tracker.Observe(Read(3, [0xA5]));

        Assert.Equal(0xFF0FEu, reset.Address);
        Assert.Equal("Software Reset", reset.SourceFields?["register_meaning"]);
        Assert.Null(read.Address);
        Assert.Equal("false", read.SourceFields?["register_page_known"]);
    }

    [Fact]
    public void Reset_evidence_prevents_a_partial_transaction_from_leaking_forward()
    {
        var tracker = new NvtRegisterTracker();

        tracker.Observe(Write(0, [0xFF, 0x09, 0x90]));
        tracker.ResetEvidence();
        tracker.Observe(Write(1, [0x00]));
        var read = tracker.Observe(Read(2, [0xA5]));

        Assert.Null(read.Address);
        Assert.Single(read.I2c?.WriteCommands ?? []);
    }

    private static SourceRecord Write(long index, IReadOnlyList<byte> data) =>
        Record(index, BusOperation.Write, data);

    private static SourceRecord Read(long index, IReadOnlyList<byte> data) =>
        Record(index, BusOperation.Read, data);

    private static SourceRecord Record(long index, BusOperation operation, IReadOnlyList<byte> data) =>
        new(
            index,
            $"test:{index}",
            DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(index),
            operation,
            "TP",
            null,
            data.Count,
            data,
            string.Empty,
            new SourceLocation(0, checked((int)index + 1)),
            new I2cTransport(1, [], [], null),
            new Dictionary<string, string> { ["adapter"] = "test" });
}
