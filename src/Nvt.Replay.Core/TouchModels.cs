namespace Nvt.Replay.Core;

public enum TouchType : byte
{
    Finger = 0,
    Glove = 1,
    Palm = 2,
    Reserved = 3,
}

public enum TouchStatus : byte
{
    NoFinger = 0,
    Enter = 1,
    Move = 2,
    Break = 3,
}
