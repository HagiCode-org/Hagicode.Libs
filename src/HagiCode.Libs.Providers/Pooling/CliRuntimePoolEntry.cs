using System.Collections.ObjectModel;
using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Pooling;

internal sealed class CliRuntimePoolEntry<TResource> : IAsyncDisposable
    where TResource : IAsyncDisposable
{
    private readonly HashSet<string> _registeredKeys = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public CliRuntimePoolEntry(
        string providerName,
        TResource resource,
        string compatibilityFingerprint,
        CliPoolSettings settings,
        TimeProvider? timeProvider = null)
    {
        ProviderName = providerName ?? throw new ArgumentNullException(nameof(providerName));
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        CompatibilityFingerprint = compatibilityFingerprint ?? throw new ArgumentNullException(nameof(compatibilityFingerprint));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Metadata = new Dictionary<string, object?>(StringComparer.Ordinal);
        Touch();
    }

    public string ProviderName { get; }

    public TResource Resource { get; }

    public string CompatibilityFingerprint { get; private set; }

    public CliPoolSettings Settings { get; }

    public SemaphoreSlim ExecutionLock { get; } = new(1, 1);

    public DateTimeOffset LastUsedAt { get; private set; }

    public bool IsLeased { get; internal set; }

    public bool IsFaulted { get; internal set; }

    public IDictionary<string, object?> Metadata { get; }

    public IReadOnlyCollection<string> RegisteredKeys => new ReadOnlyCollection<string>(_registeredKeys.ToArray());

    public void RegisterKey(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _registeredKeys.Add(key);
        }
    }

    public void Touch()
    {
        LastUsedAt = _timeProvider.GetUtcNow();
    }

    public void RefreshFingerprint(string compatibilityFingerprint)
    {
        CompatibilityFingerprint = compatibilityFingerprint ?? throw new ArgumentNullException(nameof(compatibilityFingerprint));
        Touch();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExecutionLock.Dispose();
        await Resource.DisposeAsync().ConfigureAwait(false);
    }
}
