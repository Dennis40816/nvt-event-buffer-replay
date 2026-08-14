using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Tests;

public sealed class KingstVisMotionFixtureTests
{
    [Fact]
    public async Task Fixture_replays_a_complete_multi_contact_lifecycle()
    {
        var fixture = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "fixtures",
            "kingstvis-common-0x83.csv"));

        var capture = await CaptureSession.LoadAsync(fixture);
        var report = capture.DecodeCommon(CommonEventBufferVersion.V83);
        var replay = new CommonReplaySession(report.Frames);

        Assert.Equal("kingstvis-decoded-i2c", capture.Probe.AdapterId);
        Assert.Equal(38, capture.Records.Count);
        Assert.Equal(19, report.Frames.Count);
        Assert.All(report.Frames, frame => Assert.True(frame.CrcValid));
        Assert.Equal(TimeSpan.FromMilliseconds(100), replay.FrameInterval);

        var first = replay.Seek(0);
        Assert.Equal(TouchStatus.Enter, Assert.Single(first.ReportedContacts).Status);
        Assert.Equal((byte)1, first.ReportedContacts[0].Id);

        var threeContacts = replay.Seek(6);
        Assert.Equal([1, 2, 3], threeContacts.HostContacts.Select(contact => (int)contact.Id));
        Assert.Equal(TouchStatus.Enter, threeContacts.ReportedContacts.Single(contact => contact.Id == 3).Status);

        var firstBreak = replay.Seek(13);
        Assert.Equal(TouchStatus.Break, firstBreak.ReportedContacts.Single(contact => contact.Id == 1).Status);
        Assert.Equal(2, firstBreak.Frame.NumTouches);
        var allBreak = replay.Seek(18);
        Assert.True(allBreak.Frame.AllBreak);
        Assert.Empty(allBreak.HostContacts);
        Assert.DoesNotContain(replay.Diagnostics, diagnostic => diagnostic.Code == "CAPTURE_END_ACTIVE_CONTACTS");
    }
}
