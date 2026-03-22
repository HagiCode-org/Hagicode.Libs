using System.Collections.ObjectModel;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Represents a pooled ACP session entry.
/// </summary>
public sealed class PooledAcpSessionEntry : IAsyncDisposable
{
    private readonly HashSet<string> _registeredKeys = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PooledAcpSessionEntry"/> class.
    /// </summary>
    public PooledAcpSessionEntry(
        string providerName,
        string sessionId,
        IAcpSessionClient sessionClient,
        string compatibilityFingerprint,
        AcpSessionHandle sessionHandle,
        CliPoolSettings settings,
        TimeProvider? timeProvider = null)
    {
        ProviderName = providerName ?? throw new ArgumentNullException(nameof(providerName));
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        SessionClient = sessionClient ?? throw new ArgumentNullException(nameof(sessionClient));
        CompatibilityFingerprint = compatibilityFingerprint ?? throw new ArgumentNullException(nameof(compatibilityFingerprint));
        SessionHandle = sessionHandle ?? throw new ArgumentNullException(nameof(sessionHandle));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
        Touch();
        RegisterKey(sessionId);
    }

    /// <summary>
    /// Gets the provider identity.
    /// </summary>
    public string ProviderName { get; }

    /// <summary>
    /// Gets the active ACP session identifier.
    /// </summary>
    public string SessionId { get; private set; }

    /// <summary>
    /// Gets the live ACP client.
    /// </summary>
    public IAcpSessionClient SessionClient { get; }

    /// <summary>
    /// Gets the compatibility fingerprint for the entry.
    /// </summary>
    public string CompatibilityFingerprint { get; private set; }

    /// <summary>
    /// Gets the session handle produced when the entry was created.
    /// </summary>
    public AcpSessionHandle SessionHandle { get; private set; }

    /// <summary>
    /// Gets the effective pool settings for this entry.
    /// </summary>
    public CliPoolSettings Settings { get; }

    /// <summary>
    /// Gets the execution lock for serialized prompt execution.
    /// </summary>
    public SemaphoreSlim ExecutionLock { get; } = new(1, 1);

    /// <summary>
    /// Gets the last-used timestamp.
    /// </summary>
    public DateTimeOffset LastUsedAt { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the entry is currently leased.
    /// </summary>
    public bool IsLeased { get; internal set; }

    /// <summary>
    /// Gets a value indicating whether the entry has faulted.
    /// </summary>
    public bool IsFaulted { get; internal set; }

    /// <summary>
    /// Gets the registered lookup keys.
    /// </summary>
    public IReadOnlyCollection<string> RegisteredKeys => new ReadOnlyCollection<string>(_registeredKeys.ToArray());

    /// <summary>
    /// Records that the entry was used.
    /// </summary>
    public void Touch()
    {
        LastUsedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>
    /// Registers an additional lookup key.
    /// </summary>
    public void RegisterKey(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _registeredKeys.Add(key);
        }
    }

    /// <summary>
    /// Updates the compatibility metadata after provider normalization.
    /// </summary>
    public void RefreshSession(AcpSessionHandle sessionHandle, string compatibilityFingerprint)
    {
        SessionHandle = sessionHandle ?? throw new ArgumentNullException(nameof(sessionHandle));
        SessionId = sessionHandle.SessionId;
        CompatibilityFingerprint = compatibilityFingerprint ?? throw new ArgumentNullException(nameof(compatibilityFingerprint));
        RegisterKey(sessionHandle.SessionId);
        Touch();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExecutionLock.Dispose();
        await SessionClient.DisposeAsync().ConfigureAwait(false);
    }
}
