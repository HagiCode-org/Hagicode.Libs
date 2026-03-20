namespace HagiCode.Libs.Providers.Codebuddy;

/// <summary>
/// Describes a CodeBuddy ACP CLI invocation.
/// </summary>
public sealed record CodebuddyOptions
{
    /// <summary>
    /// Gets or sets the custom CodeBuddy executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the CodeBuddy model override.
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
    /// Gets or sets environment variables injected into the CodeBuddy process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the ACP bootstrap switch.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
