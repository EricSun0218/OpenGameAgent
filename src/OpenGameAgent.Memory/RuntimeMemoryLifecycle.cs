namespace OpenGameAgent.Memory;

/// <summary>
/// Small host-facing lifecycle for status checks, explicit rebuilds, and
/// deterministic provider cleanup. It does not own game state or decide when a
/// save should advance.
/// </summary>
public sealed class RuntimeMemoryLifecycle : IAsyncDisposable
{
    private readonly VectorMemoryStore _store;
    private int _disposed;

    public RuntimeMemoryLifecycle(VectorMemoryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public VectorMemoryStore Store => _store;

    public ValueTask<VectorMemoryStatus> InspectAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _store.GetStatusAsync(sessionId, cancellationToken);
    }

    public ValueTask<VectorMemoryStatus> RebuildAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _store.RebuildAsync(sessionId, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await _store.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(RuntimeMemoryLifecycle));
        }
    }
}
