namespace HagiCode.Libs.Providers.Hermes;

using HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes a Hermes ACP CLI invocation.
/// </summary>
public sealed record HermesOptions
{
    /// <summary>
    /// Gets or sets the custom Hermes executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the Hermes model override.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the in-memory conversation key to reuse within the current provider instance.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether in-memory conversation reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets the ACP bootstrap timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the Hermes process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets the raw Hermes CLI arguments used for managed ACP startup.
    /// Tokens are boundary-trimmed individually and whitespace-only tokens are ignored.
    /// </summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Gets or sets provider-level pooling overrides.
    /// </summary>
    public CliPoolSettings? PoolSettings { get; init; }
}
