namespace HagiCode.Libs.Providers;

/// <summary>
/// Represents the result of testing a provider.
/// </summary>
public sealed record CliProviderTestResult
{
    /// <summary>
    /// Gets or sets the provider name.
    /// </summary>
    public required string ProviderName { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the test succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the detected provider version.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Gets or sets the error message when the test fails.
    /// </summary>
    public string? ErrorMessage { get; init; }
}
