using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Copilot;
using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Providers.Pooling;

/// <summary>
/// Coordinates the shared pooling backends used by built-in providers.
/// </summary>
public sealed class CliProviderPoolCoordinator : IAsyncDisposable
{
    private readonly ICliAcpSessionPool _acpPool;
    private readonly CliRuntimePool<ICliTransport> _transportPool = new();
    private readonly CliRuntimePool<ICopilotSdkRuntime> _copilotRuntimePool = new();
    private readonly CliRuntimePool<CodexPooledThreadState> _codexThreadPool = new();

    public CliProviderPoolCoordinator(ICliAcpSessionPool? acpPool = null)
    {
        _acpPool = acpPool ?? new CliAcpSessionPool();
    }

    public Task<PooledAcpSessionLease> AcquireAcpSessionAsync(
        CliAcpPoolRequest request,
        Func<CancellationToken, Task<PooledAcpSessionEntry>> entryFactory,
        CancellationToken cancellationToken = default)
        => _acpPool.AcquireAsync(request, entryFactory, cancellationToken);

    public Task<int> ReapAcpSessionsAsync(string? providerName = null, CancellationToken cancellationToken = default)
        => _acpPool.ReapIdleEntriesAsync(providerName, cancellationToken);

    public Task DisposeAcpProviderAsync(string providerName, CancellationToken cancellationToken = default)
        => _acpPool.DisposeProviderEntriesAsync(providerName, cancellationToken);

    internal Task<CliRuntimePoolLease<ICliTransport>> AcquireTransportAsync(
        CliRuntimePoolRequest request,
        Func<CancellationToken, Task<CliRuntimePoolEntry<ICliTransport>>> entryFactory,
        CancellationToken cancellationToken = default)
        => _transportPool.AcquireAsync(request, entryFactory, cancellationToken);

    internal Task<int> ReapTransportEntriesAsync(string? providerName = null, CancellationToken cancellationToken = default)
        => _transportPool.ReapIdleEntriesAsync(providerName, cancellationToken);

    internal Task DisposeTransportProviderAsync(string providerName, CancellationToken cancellationToken = default)
        => _transportPool.DisposeProviderEntriesAsync(providerName, cancellationToken);

    internal Task<CliRuntimePoolLease<ICopilotSdkRuntime>> AcquireCopilotRuntimeAsync(
        CliRuntimePoolRequest request,
        Func<CancellationToken, Task<CliRuntimePoolEntry<ICopilotSdkRuntime>>> entryFactory,
        CancellationToken cancellationToken = default)
        => _copilotRuntimePool.AcquireAsync(request, entryFactory, cancellationToken);

    internal Task<int> ReapCopilotRuntimeEntriesAsync(string? providerName = null, CancellationToken cancellationToken = default)
        => _copilotRuntimePool.ReapIdleEntriesAsync(providerName, cancellationToken);

    internal Task DisposeCopilotProviderAsync(string providerName, CancellationToken cancellationToken = default)
        => _copilotRuntimePool.DisposeProviderEntriesAsync(providerName, cancellationToken);

    internal Task<CliRuntimePoolLease<CodexPooledThreadState>> AcquireCodexThreadAsync(
        CliRuntimePoolRequest request,
        Func<CancellationToken, Task<CliRuntimePoolEntry<CodexPooledThreadState>>> entryFactory,
        CancellationToken cancellationToken = default)
        => _codexThreadPool.AcquireAsync(request, entryFactory, cancellationToken);

    internal Task<int> ReapCodexThreadEntriesAsync(string? providerName = null, CancellationToken cancellationToken = default)
        => _codexThreadPool.ReapIdleEntriesAsync(providerName, cancellationToken);

    internal Task DisposeCodexProviderAsync(string providerName, CancellationToken cancellationToken = default)
        => _codexThreadPool.DisposeProviderEntriesAsync(providerName, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _acpPool.DisposeAsync().ConfigureAwait(false);
        await _transportPool.DisposeAsync().ConfigureAwait(false);
        await _copilotRuntimePool.DisposeAsync().ConfigureAwait(false);
        await _codexThreadPool.DisposeAsync().ConfigureAwait(false);
    }
}
