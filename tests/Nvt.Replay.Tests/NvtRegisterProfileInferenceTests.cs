using Nvt.Replay.Core;
using Nvt.Replay.Sources;

namespace Nvt.Replay.Tests;

public sealed class NvtRegisterProfileInferenceTests
{
    [Fact]
    public void Switch_page_and_event_buffer_access_uniquely_infer_51927_without_mutating_records()
    {
        var records = Tracked(
            Write(0, [0xFF, 0x09, 0x90, 0x00]),
            Read(1, Enumerable.Repeat((byte)0xFF, 80).ToArray()));
        var before = records.Select(item => (item.StableId, item.RawText, item.Data)).ToArray();

        var result = NvtRegisterProfileInference.Infer(records);

        Assert.Equal(NvtRegisterProfileInferenceStatus.Unique, result.Status);
        Assert.Equal("51927", result.UniqueProfile?.IcFamily);
        Assert.Single(result.Evidence);
        Assert.Equal(0x99000u, result.Evidence[0].EventBufferAddress);
        Assert.Equal("FF 09 90 00", result.Evidence[0].SwitchPageCommand);
        Assert.Equal(before, records.Select(item => (item.StableId, item.RawText, item.Data)).ToArray());
    }

    [Fact]
    public void Shared_80800_event_buffer_requires_explicit_51929_or_51950_choice()
    {
        var records = Tracked(
            Write(0, [0xFF, 0x08, 0x08, 0x00]),
            Read(1, Enumerable.Repeat((byte)0xFF, 80).ToArray()));

        var result = NvtRegisterProfileInference.Infer(records);

        Assert.Equal(NvtRegisterProfileInferenceStatus.Ambiguous, result.Status);
        Assert.Equal(["51929/51932", "51950/51951"], result.Candidates.Select(item => item.IcFamily));
    }

    [Fact]
    public void A_profile_specific_common_buffer_access_disambiguates_shared_event_buffer()
    {
        var records = Tracked(
            Write(0, [0xFF, 0x08, 0x08, 0x00]),
            Read(1, Enumerable.Repeat((byte)0xFF, 80).ToArray()),
            Write(2, [0xFF, 0x0A, 0x52, 0x00]),
            Read(3, [0x01, 0x02]));

        var result = NvtRegisterProfileInference.Infer(records);

        Assert.Equal(NvtRegisterProfileInferenceStatus.Unique, result.Status);
        Assert.Equal("51929/51932", result.UniqueProfile?.IcFamily);
    }

    [Fact]
    public void Absolute_or_offset_only_address_without_switch_page_is_not_inferred()
    {
        var absolute = Read(0, Enumerable.Repeat((byte)0xFF, 80).ToArray()) with { Address = 0x99000 };
        var result = NvtRegisterProfileInference.Infer([absolute]);

        Assert.Equal(NvtRegisterProfileInferenceStatus.None, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Different_event_buffer_pages_are_reported_as_conflicting_instead_of_guessed()
    {
        var records = Tracked(
            Write(0, [0xFF, 0x09, 0x90, 0x00]),
            Read(1, Enumerable.Repeat((byte)0xFF, 80).ToArray()),
            Write(2, [0xFF, 0x09, 0x40, 0x00]),
            Read(3, Enumerable.Repeat((byte)0xFF, 80).ToArray()));

        var result = NvtRegisterProfileInference.Infer(records);

        Assert.Equal(NvtRegisterProfileInferenceStatus.Conflicting, result.Status);
        Assert.Contains(result.Candidates, item => item.IcFamily == "51927");
        Assert.Contains(result.Candidates, item => item.IcFamily == "51923");
    }

    private static IReadOnlyList<SourceRecord> Tracked(params SourceRecord[] input)
    {
        var tracker = new NvtRegisterTracker();
        return input.Select(tracker.Observe).ToArray();
    }

    private static SourceRecord Write(long index, IReadOnlyList<byte> data) => Record(index, BusOperation.Write, data);

    private static SourceRecord Read(long index, IReadOnlyList<byte> data) => Record(index, BusOperation.Read, data);

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
            string.Join(' ', data.Select(value => value.ToString("X2"))),
            new SourceLocation(0, checked((int)index + 1)),
            new I2cTransport(0x01, [], [], null),
            new Dictionary<string, string> { ["adapter"] = "test" });
}
