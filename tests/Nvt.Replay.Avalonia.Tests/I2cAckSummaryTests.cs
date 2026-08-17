using Nvt.Replay.Analysis;
using Nvt.Replay.Avalonia.ViewModels;
using Xunit;

namespace Nvt.Replay.Avalonia.Tests;

public sealed class I2cAckSummaryTests
{
    [Fact]
    public async Task KingstVis_golden_preserves_all_byte_acknowledgements_before_summarizing()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "kingstvis-common-0x83.csv");
        var session = await CaptureSession.LoadAsync(
            fixture,
            cancellationToken: TestContext.Current.CancellationToken);
        var reads = session.Records.Where(record => record.I2c?.Acked.Count == 80).ToArray();
        Assert.Equal(19, reads.Length);
        var acknowledgements = Assert.IsAssignableFrom<IReadOnlyList<bool>>(reads[0].I2c?.Acked);

        Assert.Equal(79, acknowledgements.Count(value => value));
        Assert.False(acknowledgements[^1]);
        Assert.Equal("ACK ×79; final NAK", I2cAckSummary.Format(acknowledgements));
        Assert.All(reads, record => Assert.Equal(80, record.I2c?.Acked.Count));
    }

    [Fact]
    public void KingstVis_read_acknowledgements_collapse_to_count_and_final_nak()
    {
        var acknowledgements = Enumerable.Repeat(true, 79).Append(false).ToArray();

        Assert.Equal("ACK ×79; final NAK", I2cAckSummary.Format(acknowledgements));
    }

    public static TheoryData<string, bool[]> SummaryCases => new()
    {
        { "-", [] },
        { "ACK ×3", [true, true, true] },
        { "ACK ×2; NAK ×2 at bytes 2, 4", [true, false, true, false] },
    };

    [Theory]
    [MemberData(nameof(SummaryCases))]
    public void Summary_preserves_counts_and_highlights_nak_positions(string expected, bool[] acknowledgements)
    {
        Assert.Equal(expected, I2cAckSummary.Format(acknowledgements));
    }
}
