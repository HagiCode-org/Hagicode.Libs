namespace HagiCode.Libs.Providers.Kimi;

using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers;

/// <summary>
/// Describes a Kimi ACP CLI invocation.
/// </summary>
public sealed record KimiOptions
{
    /// <summary>
    /// Gets or sets the custom Kimi executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the optional Kimi model override.
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
    /// Gets or sets environment variables injected into the Kimi process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets the preferred Kimi authentication method identifier.
    /// When omitted, the provider uses the first advertised method.
    /// </summary>
    public string? AuthenticationMethod { get; init; }

    /// <summary>
    /// Gets or sets the optional token copied into the bootstrap method info payload.
    /// </summary>
    public string? AuthenticationToken { get; init; }

    /// <summary>
    /// Gets or sets additional key/value pairs forwarded into the authentication method info payload.
    /// </summary>
    public IReadOnlyDictionary<string, string?> AuthenticationInfo { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the managed ACP bootstrap argument.
    /// Tokens are boundary-trimmed individually, whitespace-only tokens are ignored, and duplicate ACP launch arguments are skipped.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets provider-level pooling overrides.
    /// </summary>
    public CliPoolSettings? PoolSettings { get; init; }
}
