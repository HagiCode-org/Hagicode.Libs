namespace HagiCode.Libs.Providers.ClaudeCode;

using HagiCode.Libs.Providers;

/// <summary>
/// Describes a Claude Code CLI invocation.
/// </summary>
public sealed record ClaudeCodeOptions
{
    /// <summary>
    /// Gets or sets the custom Claude executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the Anthropic API token.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets or sets the Anthropic base URL.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets or sets the working directory.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the Claude model name.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the maximum number of turns.
    /// </summary>
    public int? MaxTurns { get; init; }

    /// <summary>
    /// Gets or sets the Claude Code effort level passed as <c>--effort</c>.
    /// Supported levels are <c>low</c>, <c>medium</c>, <c>high</c>, <c>xhigh</c>, and <c>max</c>
    /// (case-insensitive, surrounding whitespace is ignored). When omitted or empty, the CLI default applies.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// Gets or sets the system prompt.
    /// </summary>
    public string? SystemPrompt { get; init; }

    /// <summary>
    /// Gets or sets the appended system prompt.
    /// </summary>
    public string? AppendSystemPrompt { get; init; }

    /// <summary>
    /// Gets or sets the explicitly allowed tools.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicitly disallowed tools.
    /// </summary>
    public IReadOnlyList<string> DisallowedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the permission mode.
    /// </summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Gets or sets the explicit session id to restore, sent as <c>--resume &lt;id&gt;</c>.
    /// Sessions are restored by explicit id only; the provider never asks the CLI to pick the most recent conversation in the working directory.
    /// When set, <see cref="SessionId" /> is not sent.
    /// </summary>
    public string? Resume { get; init; }

    /// <summary>
    /// Gets or sets the explicit session id for a new conversation, sent as <c>--session-id &lt;id&gt;</c>.
    /// Ignored when <see cref="Resume" /> is set.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or sets directories that Claude should additionally access.
    /// </summary>
    public IReadOnlyList<string> AddDirectories { get; init; } = [];

    /// <summary>
    /// Gets or sets extra CLI arguments expressed as flag/value pairs.
    /// A <see langword="null" /> value adds a switch without a value, while non-null values are boundary-trimmed and ignored when empty after trimming.
    /// </summary>
    public IReadOnlyDictionary<string, string?> ExtraArgs { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets the inline MCP server configuration.
    /// </summary>
    public IReadOnlyDictionary<string, object?> McpServers { get; init; } = new Dictionary<string, object?>();

    /// <summary>
    /// Gets or sets the path to an MCP server configuration file.
    /// </summary>
    public string? McpServersPath { get; init; }

}