namespace HagiCode.Libs.Providers.QoderCli;

using HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes a QoderCLI ACP CLI invocation.
/// </summary>
public sealed record QoderCliOptions
{
    /// <summary>
    /// Gets or sets the custom QoderCLI executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the optional QoderCLI model override.
    /// Boundary whitespace is trimmed before forwarding, and the provider does not apply a default model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to reuse.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether session reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets the ACP bootstrap timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the QoderCLI process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the ACP bootstrap switch.
    /// Tokens are boundary-trimmed individually, whitespace-only tokens are ignored, and permission-bypass flags are skipped because the provider always forces ACP sessions into yolo mode.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets provider-level pooling overrides.
    /// </summary>
    public CliPoolSettings? PoolSettings { get; init; }
}
