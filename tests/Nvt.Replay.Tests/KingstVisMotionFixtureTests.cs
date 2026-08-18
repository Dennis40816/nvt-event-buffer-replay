using Nvt.Replay.Analysis;
using Nvt.Replay.Core;
using Nvt.Replay.Formats.Common;
using Nvt.Replay.Rendering;

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
        var cache = ReplayFrameCache.Create(replay);

        Assert.Equal("kingstvis-decoded-i2c", capture.Probe.AdapterId);
        Assert.Equal(38, capture.Records.Count);
        Assert.Equal(19, report.Frames.Count);
        Assert.All(report.Frames, frame => Assert.True(frame.CrcValid));
        Assert.Equal(TouchReplayOptions.NominalTpFrameInterval, replay.FrameInterval);
        Assert.Equal(replay.Count, cache.Count);
        Assert.Same(cache[13], cache.Snapshots[13]);

        var first = replay.Seek(0);
        Assert.Equal(TouchStatus.Enter, Assert.Single(first.ReportedContacts).Status);
        Assert.Equal((byte)1, first.ReportedContacts[0].Id);

        var threeContacts = replay.Seek(6);
        Assert.Equal([1, 2, 3], threeContacts.HostContacts.Select(contact => (int)contact.Id));
        Assert.Equal(TouchStatus.Enter, threeContacts.ReportedContacts.Single(contact => contact.Id == 3).Status);

        var firstBreak = replay.Seek(13);
        Assert.Equal(TouchStatus.Break, firstBreak.ReportedContacts.Single(contact => contact.Id == 1).Status);
        Assert.Equal(2, firstBreak.Frame.NumTouches);
        var trails = ReplaySceneFactory.BuildTrails(replay, 13);
        Assert.Equal([1, 2, 3], trails.Select(trail => (int)trail.Id));
        Assert.Equal(TouchStatus.Break, trails.Single(trail => trail.Id == 1).Points[^1].Status);
        Assert.All(trails, trail => Assert.True(trail.Points.Count >= 2));
        Assert.All(ReplaySceneFactory.BuildTrails(replay, 13, 4), trail => Assert.InRange(trail.Points.Count, 2, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReplaySceneFactory.BuildTrails(replay, 13, 11));

        var allBreak = replay.Seek(18);
        Assert.True(allBreak.Frame.AllBreak);
        Assert.Empty(allBreak.HostContacts);
        Assert.Empty(ReplaySceneFactory.BuildTrails(replay, 18));
        Assert.DoesNotContain(replay.Diagnostics, diagnostic => diagnostic.Code == "CAPTURE_END_ACTIVE_CONTACTS");

        var autoPause = ReplayAutoPauseIndex.Create(cache.Snapshots);
        Assert.Equal(
            new ReplayAutomaticPause(13, ReplayAutomaticPauseKind.Break),
            autoPause.FirstAfter(0, replay.Count - 1, pauseOnBreak: true, pauseOnAllBreak: false));
        Assert.Equal(
            new ReplayAutomaticPause(18, ReplayAutomaticPauseKind.AllBreak),
            autoPause.FirstAfter(13, replay.Count - 1, pauseOnBreak: false, pauseOnAllBreak: true));
        Assert.Null(autoPause.FirstAfter(18, replay.Count - 1, pauseOnBreak: true, pauseOnAllBreak: true));
    }
}
