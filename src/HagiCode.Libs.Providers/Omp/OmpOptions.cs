namespace HagiCode.Libs.Providers.Omp;

/// <summary>
/// Describes a one-shot OMP CLI invocation.
/// </summary>
public sealed record OmpOptions
{
    /// <summary>
    /// Gets or sets the custom OMP executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory for the OMP process.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the OMP provider name.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Gets or sets the OMP model selector.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the primary system prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Gets or sets additional system prompt fragments appended in order.
    /// </summary>
    public IReadOnlyList<string> AppendSystemPrompts { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicit OMP session identifier.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or sets the OMP session directory.
    /// </summary>
    public string? SessionDirectory { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether session persistence should be disabled.
    /// </summary>
    public bool NoSession { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether all OMP tools should be disabled.
    /// </summary>
    public bool DisableAllTools { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether only built-in OMP tools should be disabled.
    /// </summary>
    public bool DisableBuiltinTools { get; init; }

    /// <summary>
    /// Gets or sets the OMP tool allowlist.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the OMP tool denylist.
    /// </summary>
    public IReadOnlyList<string> ExcludedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the OMP thinking level.
    /// </summary>
    public string? Thinking { get; init; }

    /// <summary>
    /// Gets or sets environment variable overrides for the OMP process.
    /// A <see langword="null" /> value removes the variable from the child process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional OMP CLI arguments appended after structured flags.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];
}
