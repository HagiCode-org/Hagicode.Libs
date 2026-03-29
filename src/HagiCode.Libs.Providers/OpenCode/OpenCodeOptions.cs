namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Describes an OpenCode runtime/session invocation.
/// </summary>
public sealed record OpenCodeOptions
{
    /// <summary>
    /// Gets or sets the custom OpenCode executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the externally managed OpenCode base URL.
    /// When supplied, the provider attaches instead of launching a local runtime.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets or sets the working directory forwarded to the OpenCode HTTP API.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the optional OpenCode workspace identifier.
    /// </summary>
    public string? Workspace { get; init; }

    /// <summary>
    /// Gets or sets the optional model override.
    /// Supports either <c>&lt;provider&gt;/&lt;model&gt;</c> or slashless values.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to resume.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or sets the optional session title used when a new session is created.
    /// </summary>
    public string? SessionTitle { get; init; }

    /// <summary>
    /// Gets or sets the runtime startup timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets the HTTP request timeout.
    /// </summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into a launched OpenCode runtime.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets extra arguments appended to <c>opencode serve</c>.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
