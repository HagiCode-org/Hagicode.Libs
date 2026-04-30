namespace HagiCode.Libs.Providers;

/// <summary>
/// Defines the base contract for CLI providers.
/// </summary>
public interface ICliProvider : IAsyncDisposable
{
    /// <summary>
    /// Gets the unique provider name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether the provider executable is available.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Performs a lightweight readiness check against the provider.
    /// Implementations should rely on executable discovery, version probing, ACP startup, or equivalent
    /// non-message diagnostics instead of sending an assistant prompt.
    /// </summary>
    /// <param name="cancellationToken">Cancels the ping operation.</param>
    /// <returns>The provider test result.</returns>
    Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default);
}
