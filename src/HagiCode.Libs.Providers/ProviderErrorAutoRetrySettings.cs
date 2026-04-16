namespace HagiCode.Libs.Providers;

/// <summary>
/// Execution-local retry settings for retry-capable providers that can continue the same session or thread.
/// </summary>
public sealed record ProviderErrorAutoRetrySettings
{
    /// <summary>
    /// Canonical strategy id for the built-in retry schedule.
    /// </summary>
    public const string DefaultStrategy = "default";

    /// <summary>
    /// Fixed continuation prompt sent after a retryable provider terminal failure.
    /// </summary>
    public const string ContinuationPrompt = "lagging, try to continue your jobs according to context";

    /// <summary>
    /// Default bounded retry schedule: 10s, 20s, then 60s.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(60)
    ];

    /// <summary>
    /// Default retry settings applied when the execution snapshot omits this section.
    /// </summary>
    public static ProviderErrorAutoRetrySettings Default { get; } = new();

    /// <summary>
    /// Whether retryable terminal failures should trigger continuation attempts.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Strategy id for selecting the retry schedule.
    /// </summary>
    public string Strategy { get; init; } = DefaultStrategy;

    /// <summary>
    /// Normalizes a possibly missing or partially populated retry snapshot.
    /// </summary>
    public static ProviderErrorAutoRetrySettings Normalize(ProviderErrorAutoRetrySettings? settings)
    {
        if (settings is null)
        {
            return Default;
        }

        return new ProviderErrorAutoRetrySettings
        {
            Enabled = settings.Enabled,
            Strategy = string.IsNullOrWhiteSpace(settings.Strategy)
                ? DefaultStrategy
                : settings.Strategy.Trim()
        };
    }

    /// <summary>
    /// Returns the bounded retry schedule for the current strategy.
    /// </summary>
    public IReadOnlyList<TimeSpan> GetRetrySchedule()
    {
        return string.Equals(Strategy, DefaultStrategy, StringComparison.OrdinalIgnoreCase)
            ? DefaultRetryDelays
            : DefaultRetryDelays;
    }
}
