namespace HagiCode.Libs.Providers.Pooling;

internal sealed class CliRuntimePoolLease<TResource> : IAsyncDisposable
    where TResource : IAsyncDisposable
{
    private readonly CliRuntimePool<TResource> _pool;
    private bool _disposed;

    internal CliRuntimePoolLease(CliRuntimePool<TResource> pool, CliRuntimePoolEntry<TResource> entry, bool isWarmLease)
    {
        _pool = pool;
        Entry = entry;
        IsWarmLease = isWarmLease;
    }

    public CliRuntimePoolEntry<TResource> Entry { get; }

    public bool IsWarmLease { get; }

    public bool IsFaulted { get; set; }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _pool.ReturnAsync(this, CancellationToken.None).ConfigureAwait(false);
    }
}
