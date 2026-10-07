namespace Nvt.Replay.Avalonia;

// UI-thread only; each Enter token must be disposed once, normally by using.
internal sealed class EventSuppressionScope
{
    private int depth;

    public bool IsActive => depth > 0;

    public Scope Enter()
    {
        depth++;
        return new Scope(this);
    }

    public readonly struct Scope : IDisposable
    {
        private readonly EventSuppressionScope owner;

        internal Scope(EventSuppressionScope owner) => this.owner = owner;

        public void Dispose() => owner.depth--;
    }
}
