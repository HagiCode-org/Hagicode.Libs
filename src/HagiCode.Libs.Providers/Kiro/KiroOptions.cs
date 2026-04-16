namespace HagiCode.Libs.Providers.Kiro;

using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers;

/// <summary>
/// Describes a Kiro ACP CLI invocation.
/// </summary>
public sealed record KiroOptions
{
    /// <summary>
    /// Gets or sets the custom Kiro executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the optional Kiro model override.
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
    /// Gets or sets environment variables injected into the Kiro process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets the preferred Kiro authentication method identifier.
    /// When omitted, the provider uses the first advertised method when authentication is required.
    /// </summary>
    public string? AuthenticationMethod { get; init; }

    /// <summary>
    /// Gets or sets the optional token copied into the authentication payload.
    /// </summary>
    public string? AuthenticationToken { get; init; }

    /// <summary>
    /// Gets or sets additional key/value pairs forwarded into the authentication payload.
    /// </summary>
    public IReadOnlyDictionary<string, string?> AuthenticationInfo { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets the bootstrap RPC method used before session creation.
    /// Defaults to <c>authenticate</c> when authentication is required.
    /// </summary>
    public string? BootstrapMethod { get; init; }

    /// <summary>
    /// Gets or sets additional top-level bootstrap payload parameters forwarded to the RPC call.
    /// </summary>
    public IReadOnlyDictionary<string, string?> BootstrapParameters { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the ACP bootstrap switch.
    /// Tokens are boundary-trimmed individually, whitespace-only tokens are ignored, and duplicate ACP launch arguments are skipped.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the execution-local terminal failure auto-retry settings.
    /// </summary>
    public ProviderErrorAutoRetrySettings? ProviderErrorAutoRetry { get; init; }

    /// <summary>
    /// Gets or sets provider-level pooling overrides.
    /// </summary>
    public CliPoolSettings? PoolSettings { get; init; }
}
