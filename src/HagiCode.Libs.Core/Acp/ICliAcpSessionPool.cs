namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Defines the shared ACP session pooling contract.
/// </summary>
public interface ICliAcpSessionPool : IAsyncDisposable
{
    /// <summary>
    /// Acquires a pooled entry or creates a new one when no compatible entry exists.
    /// </summary>
    Task<PooledAcpSessionLease> AcquireAsync(
        CliAcpPoolRequest request,
        Func<CancellationToken, Task<PooledAcpSessionEntry>> entryFactory,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a lease to the pool.
    /// </summary>
    Task ReturnAsync(PooledAcpSessionLease lease, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reaps idle entries.
    /// </summary>
    Task<int> ReapIdleEntriesAsync(string? providerName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes all entries owned by the provider.
    /// </summary>
    Task DisposeProviderEntriesAsync(string providerName, CancellationToken cancellationToken = default);
}
