namespace HagiCode.Libs.Providers.Antigravity;

/// <summary>
/// Describes an Antigravity CLI invocation over the Agent Client Protocol (<c>agy acp --stdio</c>).
/// </summary>
public sealed record AntigravityOptions
{
    /// <summary>
    /// Gets or sets the custom Antigravity executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to resume with <c>session/load</c>.
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
    /// Gets or sets environment variables injected into the Antigravity process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets an authentication method identifier to invoke with <c>authenticate</c> before the session starts.
    /// When omitted, the provider never starts an interactive sign-in; it reports a sign-in requirement instead.
    /// </summary>
    public string? AuthenticationMethod { get; init; }

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the managed <c>acp --stdio</c> arguments.
    /// Tokens are boundary-trimmed individually, whitespace-only tokens are ignored, and duplicate ACP launch arguments are skipped.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
