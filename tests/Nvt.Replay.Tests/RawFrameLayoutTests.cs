using Nvt.Replay.Core;
using Nvt.Replay.Formats;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Formats.Desay97;
using Nvt.Replay.Rendering;

namespace Nvt.Replay.Tests;

public sealed class RawFrameLayoutTests
{
    [Fact]
    public void Common_0x83_keeps_each_seven_byte_touch_slot_together()
    {
        var bytes = Enumerable.Repeat((byte)0xFF, CommonEventBufferDecoder.FrameLength).ToArray();
        bytes[0] = 0;
        bytes[73] = 0;
        bytes[79] = CommonCrc8.Compute(bytes.AsSpan(0, 79));
        var source = Record(1, bytes);
        var frame = Assert.IsType<CommonEventBufferFrame>(new CommonEventBufferDecoder().Decode(source, CommonEventBufferVersion.V83).Frame);

        var layout = RawFrameLayoutBuilder.Build(source, frame);

        Assert.Equal("1-byte header, 10 × 7-byte touch slots, version tail, CRC", layout.Summary);
        Assert.Collection(
            layout.Sections.Where(section => section.Label.StartsWith("TOUCH", StringComparison.Ordinal)),
            Enumerable.Range(0, 10).Select<int, Action<RawByteSection>>(index => section =>
            {
                Assert.Equal($"TOUCH {index + 1}", section.Label);
                Assert.Equal(7, section.Bytes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
                Assert.Equal("unused", section.Detail);
            }).ToArray());
        Assert.Equal("47-48", Assert.Single(layout.Sections, section => section.Label == "BUTTON").Offset);
        Assert.Equal("49", Assert.Single(layout.Sections, section => section.Label == "ASIL").Offset);
        Assert.Equal("4A-4E", Assert.Single(layout.Sections, section => section.Label == "RESERVED").Offset);
        Assert.Equal("4F", Assert.Single(layout.Sections, section => section.Label == "CRC").Offset);
    }

    [Fact]
    public void Desay_0x97_keeps_each_five_byte_touch_slot_together()
    {
        byte[] bytes = [0x01, 0x61, 0x03, 0x14, 0x07, 0x19, 0];
        bytes[^1] = Crc8Poly1D.Compute(bytes.AsSpan(0, bytes.Length - 1));
        var probe = Record(1, [0x01]);
        var payload = Record(2, bytes);
        var packet = new Desay97AssembledPacket(probe, payload, bytes);
        var frame = Assert.IsType<Desay97Frame>(new Desay97Decoder().Decode(packet, Desay97Profile.BenzPalm).Frame);

        var layout = RawFrameLayoutBuilder.Build(payload, frame);

        Assert.Contains("two-transaction protocol", layout.Summary);
        var touch = Assert.Single(layout.Sections, section => section.Label == "TOUCH 1");
        Assert.Equal("01-05", touch.Offset);
        Assert.Equal(5, touch.Bytes.Split(' ').Length);
        Assert.Contains("ID 1", touch.Detail);
        Assert.Equal("06", Assert.Single(layout.Sections, section => section.Label == "CRC").Offset);
    }

    private static SourceRecord Record(long index, IReadOnlyList<byte> data) =>
        new(index, $"record-{index}", null, BusOperation.Read, "TP", 0x99000, data.Count, data, string.Empty, new SourceLocation(0, (int)index));
}
