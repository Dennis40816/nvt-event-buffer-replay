// Copyright (c) 2026 Dennis Liu. All rights reserved.

namespace Nvt.Replay.Avalonia.ViewModels;

/// <summary>Owns the active operation and its cancellation lifetime on the UI thread.</summary>
internal sealed class OperationOwner : IDisposable
{
    private Lease? _current;
    private long _nextId;

    internal bool Active => _current is not null;

    internal Lease? Current => _current;

    internal Lease Begin()
    {
        var previous = _current;
        var next = new Lease(checked(++_nextId));
        _current = next;
        try { previous?.Cancel(); }
        finally { previous?.Dispose(); }
        return next;
    }

    internal bool IsCurrent(Lease lease) =>
        _current is not null && ReferenceEquals(_current, lease);

    internal bool Release(Lease lease)
    {
        if (!IsCurrent(lease)) return false;
        Dispose();
        return true;
    }

    internal void CancelCurrent() => _current?.Cancel();

    public void Dispose()
    {
        _current?.Dispose();
        _current = null;
    }

    internal sealed class Lease : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();

        internal Lease(long id)
        {
            Id = id;
            // Keep the token readable after the superseding owner disposes its source.
            Token = _cancellation.Token;
        }

        internal long Id { get; }
        internal CancellationToken Token { get; }
        internal void Cancel() => _cancellation.Cancel();
        public void Dispose() => _cancellation.Dispose();
    }
}
