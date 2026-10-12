// Copyright (c) 2026 Dennis Liu. All rights reserved.

using Nvt.Replay.Analysis;
using Nvt.Replay.Core;

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>The visible inspector's source and completed presentation, owned by the UI thread.</summary>
internal sealed record InspectorSelection(
    SourceRecord Record,
    object? Frame,
    ITouchReplaySnapshot? Snapshot,
    InspectorFramePresentation Presentation);
