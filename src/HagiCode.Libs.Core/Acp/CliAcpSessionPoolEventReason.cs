namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes why a pooled ACP entry left the pool.
/// </summary>
public enum CliAcpSessionPoolEventReason
{
    /// <summary>
    /// The warm entry was incompatible with the requested fingerprint.
    /// </summary>
    CompatibilityMismatch = 0,

    /// <summary>
    /// The pool evicted an older idle entry to satisfy capacity.
    /// </summary>
    Capacity = 1,

    /// <summary>
    /// The entry aged past its idle timeout.
    /// </summary>
    Idle = 2,

    /// <summary>
    /// The entry faulted and was removed immediately.
    /// </summary>
    Fault = 3,

    /// <summary>
    /// The provider explicitly disposed its entries.
    /// </summary>
    ProviderDisposal = 4,

    /// <summary>
    /// The entire pool instance was disposed.
    /// </summary>
    PoolDisposal = 5
}
