// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Avalonia;

namespace Nvt.Replay.Tests;

internal sealed class UiFailureTestProbe
{
    // Owned by each test; no process-wide state.
    internal List<(string Operation, string Status)> Reports { get; } = [];
    internal List<(string Operation, Exception Error)> Fallbacks { get; } = [];

    internal UiFailureReporter CreateReporter(
        Action<string, string>? publish = null,
        Action<string, Exception>? fallback = null,
        Action? resetPreview = null, Action? refreshSetup = null) => new(new(
            TimeProvider.System, publish ?? Publish, fallback ?? RecordFallback, resetPreview, refreshSetup));

    private void Publish(string operation, string status) => Reports.Add((operation, status));
    private void RecordFallback(string operation, Exception error) => Fallbacks.Add((operation, error));
}
