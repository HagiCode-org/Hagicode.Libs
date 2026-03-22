namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents a read-only diagnostics snapshot for the shared ACP session pool.
/// </summary>
/// <param name="HitCount">The number of warm-entry reuse hits.</param>
/// <param name="MissCount">The number of acquires that had to create a fresh entry.</param>
/// <param name="EvictionCount">The total number of entries evicted from the pool.</param>
/// <param name="FaultCount">The total number of entries evicted because they faulted.</param>
/// <param name="ActiveEntryCount">The number of entries currently registered in the pool.</param>
/// <param name="LeasedEntryCount">The number of entries currently leased.</param>
/// <param name="IndexedKeyCount">The number of lookup keys currently indexed.</param>
/// <param name="ProviderDiagnostics">Provider-scoped counters and recent events.</param>
/// <param name="LastEviction">The most recent eviction event across all providers, when available.</param>
/// <param name="LastFault">The most recent fault event across all providers, when available.</param>
public sealed record CliAcpSessionPoolDiagnostics(
    long HitCount,
    long MissCount,
    long EvictionCount,
    long FaultCount,
    int ActiveEntryCount,
    int LeasedEntryCount,
    int IndexedKeyCount,
    IReadOnlyList<CliAcpSessionPoolProviderDiagnostics> ProviderDiagnostics,
    CliAcpSessionPoolRecentEventDiagnostics? LastEviction,
    CliAcpSessionPoolRecentEventDiagnostics? LastFault);
