using Nvt.Replay.Core;
using Nvt.Replay.Formats.Desay97;

namespace Nvt.Replay.Analysis;

/// <summary>Reconstructs touch replay snapshots and host contact state from Desay 0x97 frames.</summary>
public sealed class Desay97ReplaySession : TouchReplaySession<Desay97Frame>
{
    /// <summary>Creates a replay session for Desay 0x97 frames with optional touch replay settings.</summary>
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
