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
    /// Performs a lightweight health check against the provider.
    /// </summary>
    /// <param name="cancellationToken">Cancels the ping operation.</param>
    /// <returns>The provider test result.</returns>
    Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default);
}
