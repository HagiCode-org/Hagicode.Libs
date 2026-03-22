namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents provider-scoped diagnostics for the shared ACP session pool.
/// </summary>
/// <param name="ProviderName">The provider name.</param>
/// <param name="HitCount">The number of warm-entry reuse hits for the provider.</param>
/// <param name="MissCount">The number of fresh-entry misses for the provider.</param>
/// <param name="EvictionCount">The number of provider entries evicted from the pool.</param>
/// <param name="FaultCount">The number of provider entries evicted because they faulted.</param>
/// <param name="ActiveEntryCount">The number of active entries registered for the provider.</param>
/// <param name="LeasedEntryCount">The number of provider entries currently leased.</param>
/// <param name="IndexedKeyCount">The number of lookup keys currently indexed for the provider.</param>
/// <param name="LastEviction">The most recent eviction event for the provider, when available.</param>
/// <param name="LastFault">The most recent fault event for the provider, when available.</param>
public sealed record CliAcpSessionPoolProviderDiagnostics(
    string ProviderName,
    long HitCount,
    long MissCount,
    long EvictionCount,
    long FaultCount,
    int ActiveEntryCount,
    int LeasedEntryCount,
    int IndexedKeyCount,
    CliAcpSessionPoolRecentEventDiagnostics? LastEviction,
    CliAcpSessionPoolRecentEventDiagnostics? LastFault);
