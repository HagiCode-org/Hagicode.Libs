namespace HagiCode.Libs.Providers.Codex;

using HagiCode.Libs.Core.Acp;

/// <summary>
/// Describes a Codex CLI invocation.
/// </summary>
public sealed record CodexOptions
{
    /// <summary>
    /// Gets or sets the custom Codex executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the OpenAI API key override.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets or sets the OpenAI base URL override.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets or sets the working directory passed to Codex.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the Codex model name.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the sandbox mode.
    /// </summary>
    public string? SandboxMode { get; init; }

    /// <summary>
    /// Gets or sets the approval policy.
    /// </summary>
    public string? ApprovalPolicy { get; init; }

    /// <summary>
    /// Gets or sets the optional Codex configuration profile.
    /// <see langword="null" />, empty, or whitespace-only values are treated as unspecified and do not emit <c>-p</c>.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>
    /// Gets or sets the thread id to resume.
    /// </summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// Gets or sets the caller-scoped logical session identity used for pooled entry isolation.
    /// The value should remain stable for the same resumed Codex session and differ across parallel sessions.
    /// </summary>
    public string? LogicalSessionKey { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the git repository check should be skipped.
    /// </summary>
    public bool SkipGitRepositoryCheck { get; init; }

    /// <summary>
    /// Gets or sets directories that Codex should additionally access.
    /// </summary>
    public IReadOnlyList<string> AddDirectories { get; init; } = [];

    /// <summary>
    /// Gets or sets environment variables injected into the Codex process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets discrete Codex config override entries.
    /// Each item should represent exactly one TOML assignment and will be emitted as its own <c>--config</c>.
    /// </summary>
    public IReadOnlyList<string> ConfigOverrides { get; init; } = [];

    /// <summary>
    /// Gets or sets extra CLI arguments expressed as flag/value pairs.
    /// A <see langword="null" /> value adds a switch without a value, while non-null values are boundary-trimmed and ignored when empty after trimming.
    /// </summary>
    public IReadOnlyDictionary<string, string?> ExtraArgs { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets provider-level pooling overrides.
    /// </summary>
    public CliPoolSettings? PoolSettings { get; init; }
}
