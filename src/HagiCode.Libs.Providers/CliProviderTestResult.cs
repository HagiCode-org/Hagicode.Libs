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

    /// <summary>
    /// Gets or sets the validation mode that produced this diagnostic.
    /// </summary>
    public string? ValidationMode { get; init; }

    /// <summary>
    /// Gets or sets the model identifier that was explicitly validated.
    /// </summary>
    public string? CheckedModel { get; init; }

    /// <summary>
    /// Gets or sets whether a model-aware validation passed.
    /// </summary>
    public bool? ValidationPassed { get; init; }

    /// <summary>
    /// Gets or sets the expected normalized response for model-aware validation.
    /// </summary>
    public string? ExpectedResponse { get; init; }

    /// <summary>
    /// Gets or sets the raw provider response that was validated.
    /// </summary>
    public string? ActualResponse { get; init; }

    /// <summary>
    /// Gets or sets the normalized provider response used for exact comparison.
    /// </summary>
    public string? NormalizedResponse { get; init; }
}
