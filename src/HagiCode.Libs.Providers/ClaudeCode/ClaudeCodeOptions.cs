namespace HagiCode.Libs.Providers.ClaudeCode;

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
    /// Gets or sets a value indicating whether the conversation should continue.
    /// </summary>
    public bool ContinueConversation { get; init; }

    /// <summary>
    /// Gets or sets the resume token or session id to continue from.
    /// </summary>
    public string? Resume { get; init; }

    /// <summary>
    /// Gets or sets the explicit session id.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or sets directories that Claude should additionally access.
    /// </summary>
    public IReadOnlyList<string> AddDirectories { get; init; } = [];

    /// <summary>
    /// Gets or sets extra CLI arguments expressed as flag/value pairs.
    /// A <see langword="null" /> value adds a switch without a value.
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
