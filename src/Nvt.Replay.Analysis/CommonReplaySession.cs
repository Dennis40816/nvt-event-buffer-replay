using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Analysis;

public sealed class CommonReplaySession : TouchReplaySession<CommonEventBufferFrame>
{
    public CommonReplaySession(IReadOnlyList<CommonEventBufferFrame> frames, TouchReplayOptions? options = null)
        : base(frames, Project, options)
    {
    }

    private static TouchReplayFrameProjection Project(CommonEventBufferFrame frame)
    {
        var contacts = frame.Fingers
            .Where(finger => finger.IsReported)
            .Select(finger => new ReplayContact(
                finger.Id,
                finger.Type,
                finger.Status,
                finger.X,
                finger.Y,
                frame.Source.StableId))
            .ToArray();
        return new TouchReplayFrameProjection(
            frame.Source,
            [frame.Source],
            contacts,
            contacts,
            frame.AllBreak,
            frame.DiagnosticPacket?.PalmOn == true,
            frame.HostStateEligible);
    }
}
