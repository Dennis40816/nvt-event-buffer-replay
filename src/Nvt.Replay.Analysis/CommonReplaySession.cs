using Nvt.Replay.Formats.Common;

namespace Nvt.Replay.Analysis;

/// <summary>Reconstructs touch replay snapshots and host contact state from Common Event Buffer frames.</summary>
public sealed class CommonReplaySession : TouchReplaySession<CommonEventBufferFrame>
{
    /// <summary>Creates a replay session for Common Event Buffer frames with optional touch replay settings.</summary>
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
