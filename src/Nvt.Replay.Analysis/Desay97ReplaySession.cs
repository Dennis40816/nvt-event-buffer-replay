using Nvt.Replay.Core;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Analysis;

public sealed class Desay97ReplaySession : TouchReplaySession<Desay97Frame>
{
    public Desay97ReplaySession(IReadOnlyList<Desay97Frame> frames, TouchReplayOptions? options = null)
        : base(frames, Project, options)
    {
    }

    private static TouchReplayFrameProjection Project(Desay97Frame frame)
    {
        var reported = frame.Fingers
            .Where(finger => finger.Status != TouchStatus.NoFinger || finger.Palm || finger.Invalid)
            .Select(finger => new ReplayContact(
                finger.Id,
                finger.Palm ? TouchType.Palm : TouchType.Finger,
                finger.Status,
                finger.X,
                finger.Y,
                frame.Packet.StableId,
                finger.Invalid))
            .ToArray();
        return new TouchReplayFrameProjection(
            frame.Source,
            frame.Packet.PhysicalRecords,
            reported,
            reported.Where(contact => !contact.Invalid).ToArray(),
            frame.AllBreak,
            false,
            frame.HostStateEligible);
    }
}
