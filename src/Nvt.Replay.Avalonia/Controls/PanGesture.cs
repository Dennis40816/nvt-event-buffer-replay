// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Avalonia;
using Avalonia.Input;

namespace Nvt.Replay.Avalonia.Controls;

/// <summary>The pointer drag that pans a zoomed paint surface. UI thread only.</summary>
internal sealed class PanGesture
{
    internal bool IsPanning { get; set; }

    internal Point Start { get; set; }

    internal Vector StartOffset { get; set; }

    internal IPointer? Pointer { get; set; }
}
