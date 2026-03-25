using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Pooling;

internal sealed class CliRuntimePool<TResource> : IAsyncDisposable
    where TResource : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CliRuntimePoolEntry<TResource>> _keyIndex = new(StringComparer.Ordinal);
    private readonly HashSet<CliRuntimePoolEntry<TResource>> _entries = [];
    private bool _disposed;

    public async Task<CliRuntimePoolLease<TResource>> AcquireAsync(
        CliRuntimePoolRequest request,
        Func<CancellationToken, Task<CliRuntimePoolEntry<TResource>>> entryFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entryFactory);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReapIdleEntriesUnsafeAsync(request.ProviderName).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.LogicalSessionKey) &&
                _keyIndex.TryGetValue(request.LogicalSessionKey, out var existingEntry))
            {
                if (CanReuse(existingEntry, request))
                {
                    existingEntry.IsLeased = true;
                    existingEntry.Touch();
                    return new CliRuntimePoolLease<TResource>(this, existingEntry, true);
                }

                await RemoveEntryUnsafeAsync(existingEntry).ConfigureAwait(false);
            }

            await EnforceCapacityUnsafeAsync(request.ProviderName, request.Settings).ConfigureAwait(false);
            var createdEntry = await entryFactory(cancellationToken).ConfigureAwait(false);
            createdEntry.RegisterKey(request.LogicalSessionKey);
            createdEntry.Touch();
            createdEntry.IsLeased = true;
            _entries.Add(createdEntry);
            IndexEntry(createdEntry);
            return new CliRuntimePoolLease<TResource>(this, createdEntry, false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReturnAsync(CliRuntimePoolLease<TResource> lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = lease.Entry;
            entry.IsLeased = false;
            entry.Touch();
            if (lease.IsFaulted)
            {
                entry.IsFaulted = true;
                await RemoveEntryUnsafeAsync(entry).ConfigureAwait(false);
                return;
            }

            await ReapIdleEntriesUnsafeAsync(entry.ProviderName).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RegisterKeyAsync(
        CliRuntimePoolEntry<TResource> entry,
        string? key,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_disposed)
        {
            return;
        }

        var normalizedKey = NormalizeKey(key);
        if (normalizedKey is null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_entries.Contains(entry))
            {
                return;
            }

            if (_keyIndex.TryGetValue(normalizedKey, out var existingEntry) &&
                !ReferenceEquals(existingEntry, entry))
            {
                throw new InvalidOperationException(
                    $"The pooled provider '{entry.ProviderName}' already maps key '{normalizedKey}' to another entry.");
            }

            entry.RegisterKey(normalizedKey);
            entry.Touch();
            _keyIndex[normalizedKey] = entry;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ReapIdleEntriesAsync(string? providerName = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return 0;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReapIdleEntriesUnsafeAsync(providerName).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisposeProviderEntriesAsync(string providerName, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var matchingEntries = _entries.Where(entry => string.Equals(entry.ProviderName, providerName, StringComparison.Ordinal)).ToArray();
            foreach (var entry in matchingEntries)
            {
                await RemoveEntryUnsafeAsync(entry).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            var entries = _entries.ToArray();
            _entries.Clear();
            _keyIndex.Clear();
            foreach (var entry in entries)
            {
                await entry.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static bool CanReuse(CliRuntimePoolEntry<TResource> entry, CliRuntimePoolRequest request)
    {
        return string.Equals(entry.ProviderName, request.ProviderName, StringComparison.Ordinal)
               && !entry.IsFaulted
               && string.Equals(entry.CompatibilityFingerprint, request.CompatibilityFingerprint, StringComparison.Ordinal);
    }

    private async Task EnforceCapacityUnsafeAsync(string providerName, CliPoolSettings settings)
    {
        var providerEntries = _entries.Where(entry => string.Equals(entry.ProviderName, providerName, StringComparison.Ordinal)).ToArray();
        if (providerEntries.Length < settings.MaxActiveSessions)
        {
            return;
        }

        var evictionCandidate = providerEntries
            .Where(entry => !entry.IsLeased)
            .OrderBy(entry => entry.LastUsedAt)
            .FirstOrDefault();
        if (evictionCandidate is null)
        {
            throw new InvalidOperationException($"The pooled provider '{providerName}' has reached its maximum active session limit of {settings.MaxActiveSessions}.");
        }

        await RemoveEntryUnsafeAsync(evictionCandidate).ConfigureAwait(false);
    }

    private async Task<int> ReapIdleEntriesUnsafeAsync(string? providerName)
    {
        var now = DateTimeOffset.UtcNow;
        var toRemove = new List<CliRuntimePoolEntry<TResource>>();
        foreach (var entry in _entries)
        {
            if (providerName is not null && !string.Equals(entry.ProviderName, providerName, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.IsLeased)
            {
                continue;
            }

            if (entry.IsFaulted || now - entry.LastUsedAt >= entry.Settings.IdleTimeout)
            {
                toRemove.Add(entry);
            }
        }

        foreach (var entry in toRemove)
        {
            await RemoveEntryUnsafeAsync(entry).ConfigureAwait(false);
        }

        return toRemove.Count;
    }

    private static string? NormalizeKey(string? key)
    {
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    private void IndexEntry(CliRuntimePoolEntry<TResource> entry)
    {
        foreach (var key in entry.RegisteredKeys)
        {
            _keyIndex[key] = entry;
        }
    }

    private async Task RemoveEntryUnsafeAsync(CliRuntimePoolEntry<TResource> entry)
    {
        if (!_entries.Remove(entry))
        {
            return;
        }

        foreach (var key in entry.RegisteredKeys)
        {
            if (_keyIndex.TryGetValue(key, out var indexedEntry) && ReferenceEquals(indexedEntry, entry))
            {
                _keyIndex.Remove(key);
            }
        }

        await entry.DisposeAsync().ConfigureAwait(false);
    }
}
