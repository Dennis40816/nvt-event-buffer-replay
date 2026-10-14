namespace Nvt.Replay.Core;

/// <summary>Defines the touch classifications stored in decoded contact reports.</summary>
public enum TouchType : byte
{
    /// <summary>Identifies a contact reported as a finger.</summary>
    Finger = 0,
    /// <summary>Identifies a contact reported as a gloved finger.</summary>
    Glove = 1,
    /// <summary>Identifies a contact reported as a palm.</summary>
    Palm = 2,
    /// <summary>Represents the reserved touch-type value 3.</summary>
    Reserved = 3,
}

/// <summary>Defines the status values stored in decoded contact reports.</summary>
public enum TouchStatus : byte
{
    /// <summary>Identifies a contact reported with no finger present.</summary>
    NoFinger = 0,
    /// <summary>Identifies a contact reported as entering.</summary>
    Enter = 1,
    /// <summary>Identifies a contact reported as moving.</summary>
    Move = 2,
    /// <summary>Identifies a contact reported as released.</summary>
    Break = 3,
}
