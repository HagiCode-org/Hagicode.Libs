namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents an active pooled ACP lease.
/// </summary>
public sealed class PooledAcpSessionLease : IAsyncDisposable
{
    private readonly ICliAcpSessionPool _pool;
    private bool _disposed;

    internal PooledAcpSessionLease(ICliAcpSessionPool pool, PooledAcpSessionEntry entry, bool isWarmLease)
    {
        _pool = pool;
        Entry = entry;
        IsWarmLease = isWarmLease;
    }

    /// <summary>
    /// Gets the leased entry.
    /// </summary>
    public PooledAcpSessionEntry Entry { get; }

    /// <summary>
    /// Gets a value indicating whether the lease reused a warm entry.
    /// </summary>
    public bool IsWarmLease { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the lease should be faulted on return.
    /// </summary>
    public bool IsFaulted { get; set; }

    /// <inheritdoc />
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
