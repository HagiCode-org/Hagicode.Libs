using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Provides the default shared ACP pooling implementation.
/// </summary>
public sealed class CliAcpSessionPool : ICliAcpSessionPool
{
    private const string MeterName = "HagiCode.Libs.Core.Acp.CliAcpSessionPool";
    private static readonly Meter DiagnosticsMeter = new(MeterName);
    private static readonly Counter<long> HitCounter = DiagnosticsMeter.CreateCounter<long>(
        "hagicode.cli_acp_session_pool.hit",
        description: "Counts warm ACP pool reuses.");
    private static readonly Counter<long> MissCounter = DiagnosticsMeter.CreateCounter<long>(
        "hagicode.cli_acp_session_pool.miss",
        description: "Counts ACP pool acquires that create a fresh entry.");
    private static readonly Counter<long> EvictionCounter = DiagnosticsMeter.CreateCounter<long>(
        "hagicode.cli_acp_session_pool.evict",
        description: "Counts ACP pool evictions.");
    private static readonly Counter<long> FaultCounter = DiagnosticsMeter.CreateCounter<long>(
        "hagicode.cli_acp_session_pool.fault",
        description: "Counts ACP pool faults.");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PooledAcpSessionEntry> _keyIndex = new(StringComparer.Ordinal);
    private readonly HashSet<PooledAcpSessionEntry> _entries = [];
    private readonly Dictionary<string, ProviderDiagnosticsState> _providerDiagnostics = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CliAcpSessionPool>? _logger;
    private long _hitCount;
    private long _missCount;
    private long _evictionCount;
    private long _faultCount;
    private CliAcpSessionPoolRecentEventDiagnostics? _lastEviction;
    private CliAcpSessionPoolRecentEventDiagnostics? _lastFault;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliAcpSessionPool"/> class.
    /// </summary>
    public CliAcpSessionPool(TimeProvider? timeProvider = null, ILogger<CliAcpSessionPool>? logger = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <inheritdoc />
    public CliAcpSessionPoolDiagnostics GetDiagnosticsSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _gate.Wait();
        try
        {
            var activeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var leasedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in _entries)
            {
                IncrementCount(activeCounts, entry.ProviderName);
                if (entry.IsLeased)
                {
                    IncrementCount(leasedCounts, entry.ProviderName);
                }
            }

            var indexedKeyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var keyIndexEntry in _keyIndex)
            {
                IncrementCount(indexedKeyCounts, keyIndexEntry.Value.ProviderName);
            }

            var providerNames = new HashSet<string>(_providerDiagnostics.Keys, StringComparer.Ordinal);
            foreach (var providerName in activeCounts.Keys)
            {
                providerNames.Add(providerName);
            }

            foreach (var providerName in indexedKeyCounts.Keys)
            {
                providerNames.Add(providerName);
            }

            var providerDiagnostics = providerNames
                .OrderBy(static providerName => providerName, StringComparer.Ordinal)
                .Select(providerName =>
                {
                    _providerDiagnostics.TryGetValue(providerName, out var state);
                    return new CliAcpSessionPoolProviderDiagnostics(
                        ProviderName: providerName,
                        HitCount: state?.HitCount ?? 0,
                        MissCount: state?.MissCount ?? 0,
                        EvictionCount: state?.EvictionCount ?? 0,
                        FaultCount: state?.FaultCount ?? 0,
                        ActiveEntryCount: activeCounts.GetValueOrDefault(providerName),
                        LeasedEntryCount: leasedCounts.GetValueOrDefault(providerName),
                        IndexedKeyCount: indexedKeyCounts.GetValueOrDefault(providerName),
                        LastEviction: state?.LastEviction,
                        LastFault: state?.LastFault);
                })
                .ToArray();

            return new CliAcpSessionPoolDiagnostics(
                HitCount: _hitCount,
                MissCount: _missCount,
                EvictionCount: _evictionCount,
                FaultCount: _faultCount,
                ActiveEntryCount: _entries.Count,
                LeasedEntryCount: _entries.Count(static entry => entry.IsLeased),
                IndexedKeyCount: _keyIndex.Count,
                ProviderDiagnostics: providerDiagnostics,
                LastEviction: _lastEviction,
                LastFault: _lastFault);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<PooledAcpSessionLease> AcquireAsync(
        CliAcpPoolRequest request,
        Func<CancellationToken, Task<PooledAcpSessionEntry>> entryFactory,
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
                    RecordHitUnsafe(existingEntry.ProviderName, existingEntry.SessionId);
                    existingEntry.IsLeased = true;
                    existingEntry.Touch();
                    return new PooledAcpSessionLease(this, existingEntry, isWarmLease: true);
                }

                await RemoveEntryUnsafeAsync(existingEntry, CliAcpSessionPoolEventReason.CompatibilityMismatch).ConfigureAwait(false);
            }

            await EnforceCapacityUnsafeAsync(request.ProviderName, request.Settings).ConfigureAwait(false);
            RecordMissUnsafe(request.ProviderName);

            var createdEntry = await entryFactory(cancellationToken).ConfigureAwait(false);
            createdEntry.RegisterKey(request.LogicalSessionKey);
            createdEntry.Touch();
            createdEntry.IsLeased = true;
            _entries.Add(createdEntry);
            IndexEntry(createdEntry);
            LogColdLease(createdEntry);
            return new PooledAcpSessionLease(this, createdEntry, isWarmLease: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ReturnAsync(PooledAcpSessionLease lease, CancellationToken cancellationToken = default)
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
                await RemoveEntryUnsafeAsync(entry, CliAcpSessionPoolEventReason.Fault).ConfigureAwait(false);
                return;
            }

            await ReapIdleEntriesUnsafeAsync(entry.ProviderName).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task DisposeProviderEntriesAsync(string providerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
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
                await RemoveEntryUnsafeAsync(entry, CliAcpSessionPoolEventReason.ProviderDisposal).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
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
            foreach (var entry in _entries.ToArray())
            {
                await RemoveEntryUnsafeAsync(entry, CliAcpSessionPoolEventReason.PoolDisposal).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static void IncrementCount(IDictionary<string, int> counts, string providerName)
    {
        counts.TryGetValue(providerName, out var existingCount);
        counts[providerName] = existingCount + 1;
    }

    private bool CanReuse(PooledAcpSessionEntry entry, CliAcpPoolRequest request)
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

        await RemoveEntryUnsafeAsync(evictionCandidate, CliAcpSessionPoolEventReason.Capacity).ConfigureAwait(false);
    }

    private async Task<int> ReapIdleEntriesUnsafeAsync(string? providerName)
    {
        var now = _timeProvider.GetUtcNow();
        var toRemove = new List<PooledAcpSessionEntry>();
        foreach (var entry in _entries)
        {
            if (providerName is not null && !string.Equals(entry.ProviderName, providerName, StringComparison.Ordinal))
            {
                continue;
            }

            var idleTimeout = entry.IsFaulted
                ? TimeSpan.Zero
                : entry.Settings.IdleTimeout;
            if (entry.IsLeased)
            {
                continue;
            }

            if (entry.IsFaulted || now - entry.LastUsedAt >= idleTimeout)
            {
                toRemove.Add(entry);
            }
        }

        foreach (var entry in toRemove)
        {
            await RemoveEntryUnsafeAsync(
                entry,
                entry.IsFaulted ? CliAcpSessionPoolEventReason.Fault : CliAcpSessionPoolEventReason.Idle).ConfigureAwait(false);
        }

        return toRemove.Count;
    }

    private void IndexEntry(PooledAcpSessionEntry entry)
    {
        foreach (var key in entry.RegisteredKeys)
        {
            _keyIndex[key] = entry;
        }
    }

    private ProviderDiagnosticsState GetOrAddProviderDiagnosticsUnsafe(string providerName)
    {
        if (!_providerDiagnostics.TryGetValue(providerName, out var state))
        {
            state = new ProviderDiagnosticsState();
            _providerDiagnostics[providerName] = state;
        }

        return state;
    }

    private void RecordHitUnsafe(string providerName, string sessionId)
    {
        _hitCount++;
        GetOrAddProviderDiagnosticsUnsafe(providerName).HitCount++;
        HitCounter.Add(1, CreateProviderTags(providerName));
        _logger?.LogDebug(
            "CLI ACP pool reused warm entry for provider {ProviderName} session {SessionId}. HitCount={HitCount} ActiveEntries={ActiveEntryCount} IndexedKeys={IndexedKeyCount}",
            providerName,
            sessionId,
            _hitCount,
            _entries.Count,
            _keyIndex.Count);
    }

    private void RecordMissUnsafe(string providerName)
    {
        _missCount++;
        GetOrAddProviderDiagnosticsUnsafe(providerName).MissCount++;
        MissCounter.Add(1, CreateProviderTags(providerName));
    }

    private void LogColdLease(PooledAcpSessionEntry entry)
    {
        _logger?.LogDebug(
            "CLI ACP pool created cold entry for provider {ProviderName} session {SessionId}. MissCount={MissCount} ActiveEntries={ActiveEntryCount} IndexedKeys={IndexedKeyCount}",
            entry.ProviderName,
            entry.SessionId,
            _missCount,
            _entries.Count,
            _keyIndex.Count);
    }

    private async Task RemoveEntryUnsafeAsync(PooledAcpSessionEntry entry, CliAcpSessionPoolEventReason reason)
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

        var providerDiagnostics = GetOrAddProviderDiagnosticsUnsafe(entry.ProviderName);
        var recentEvent = new CliAcpSessionPoolRecentEventDiagnostics(
            entry.ProviderName,
            entry.SessionId,
            reason,
            _timeProvider.GetUtcNow());

        _evictionCount++;
        providerDiagnostics.EvictionCount++;
        _lastEviction = recentEvent;
        providerDiagnostics.LastEviction = recentEvent;
        EvictionCounter.Add(1, CreateProviderAndReasonTags(entry.ProviderName, reason));

        if (reason == CliAcpSessionPoolEventReason.Fault)
        {
            _faultCount++;
            providerDiagnostics.FaultCount++;
            _lastFault = recentEvent;
            providerDiagnostics.LastFault = recentEvent;
            FaultCounter.Add(1, CreateProviderAndReasonTags(entry.ProviderName, reason));
            _logger?.LogWarning(
                "CLI ACP pool faulted entry for provider {ProviderName} session {SessionId}. Reason={Reason} FaultCount={FaultCount} ActiveEntries={ActiveEntryCount} IndexedKeys={IndexedKeyCount}",
                entry.ProviderName,
                entry.SessionId,
                reason,
                _faultCount,
                _entries.Count,
                _keyIndex.Count);
        }
        else
        {
            _logger?.LogInformation(
                "CLI ACP pool evicted entry for provider {ProviderName} session {SessionId}. Reason={Reason} EvictionCount={EvictionCount} ActiveEntries={ActiveEntryCount} IndexedKeys={IndexedKeyCount}",
                entry.ProviderName,
                entry.SessionId,
                reason,
                _evictionCount,
                _entries.Count,
                _keyIndex.Count);
        }

        await entry.DisposeAsync().ConfigureAwait(false);
    }

    private static TagList CreateProviderTags(string providerName)
    {
        return new TagList
        {
            { "provider", providerName }
        };
    }

    private static TagList CreateProviderAndReasonTags(string providerName, CliAcpSessionPoolEventReason reason)
    {
        return new TagList
        {
            { "provider", providerName },
            { "reason", reason.ToString() }
        };
    }

    private sealed class ProviderDiagnosticsState
    {
        public long HitCount { get; set; }

        public long MissCount { get; set; }

        public long EvictionCount { get; set; }

        public long FaultCount { get; set; }

        public CliAcpSessionPoolRecentEventDiagnostics? LastEviction { get; set; }

        public CliAcpSessionPoolRecentEventDiagnostics? LastFault { get; set; }
    }
}
