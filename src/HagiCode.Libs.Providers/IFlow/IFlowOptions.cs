namespace HagiCode.Libs.Providers.IFlow;

/// <summary>
/// Describes an IFlow ACP invocation.
/// </summary>
public sealed record IFlowOptions
{
    /// <summary>
    /// Gets or sets the custom IFlow executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the ACP endpoint to reuse instead of starting a managed process.
    /// </summary>
    public Uri? Endpoint { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the IFlow model override.
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
    /// Gets or sets environment variables injected into the IFlow process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the managed ACP bootstrap switches.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
