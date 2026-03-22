namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes provider-scoped pooling behavior.
/// </summary>
public sealed record CliPoolSettings
{
    /// <summary>
    /// Default idle timeout applied to pooled entries.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Default upper bound for pooled entries per provider.
    /// </summary>
    public static readonly int DefaultMaxActiveSessions = 8;

    /// <summary>
    /// Gets or sets a value indicating whether pooling is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Gets or sets the idle timeout after which unused entries can be reaped.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>
    /// Gets or sets the maximum number of live entries kept for one provider.
    /// </summary>
    public int MaxActiveSessions { get; init; } = DefaultMaxActiveSessions;

    /// <summary>
    /// Gets or sets the minimum warm entry hint for the provider.
    /// The current implementation creates entries lazily and keeps this value for diagnostics/documentation.
    /// </summary>
    public int MinimumWarmSessions { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether anonymous sessions may be retained after execution.
    /// </summary>
    public bool KeepAnonymousSessions { get; init; } = true;

    /// <summary>
    /// Merges provider defaults with a caller override.
    /// </summary>
    /// <param name="defaults">The provider defaults.</param>
    /// <param name="overrides">The caller override.</param>
    /// <returns>The merged settings.</returns>
    public static CliPoolSettings Merge(CliPoolSettings? defaults, CliPoolSettings? overrides)
    {
        var baseSettings = defaults ?? new CliPoolSettings();
        if (overrides is null)
        {
            return baseSettings;
        }

        return new CliPoolSettings
        {
            Enabled = overrides.Enabled,
            IdleTimeout = overrides.IdleTimeout <= TimeSpan.Zero ? baseSettings.IdleTimeout : overrides.IdleTimeout,
            MaxActiveSessions = overrides.MaxActiveSessions <= 0 ? baseSettings.MaxActiveSessions : overrides.MaxActiveSessions,
            MinimumWarmSessions = overrides.MinimumWarmSessions < 0 ? baseSettings.MinimumWarmSessions : overrides.MinimumWarmSessions,
            KeepAnonymousSessions = overrides.KeepAnonymousSessions
        };
    }
}
