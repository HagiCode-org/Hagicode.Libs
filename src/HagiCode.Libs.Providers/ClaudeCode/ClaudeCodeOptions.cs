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
    /// Gets or sets the thinking display mode passed as <c>--thinking-display</c>.
    /// Supported modes are <c>summarized</c>, <c>omitted</c>, and <c>highlights</c>
    /// (case-insensitive, surrounding whitespace is ignored). When omitted or empty, no flag is sent and the CLI
    /// and model default applies, which for current models streams thinking blocks with empty text.
    /// The flag is hidden from <c>claude --help</c>. A CLI that rejects it at launch is retried once without it.
    /// </summary>
    public string? ThinkingDisplay { get; init; }

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

    /// <summary>
    /// Gets or sets how long the run stays open after a <c>result</c> while a background subagent has reported back but
    /// a background command or monitor it started is still running. The CLI resumes the subagent when that work
    /// finishes, so ending the run earlier kills it and loses the subagent's real result.
    /// The default is 20 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> to wait without a limit.
    /// When the limit passes the run ends with the results already received.
    /// </summary>
    public TimeSpan SubagentBackgroundWorkTimeout { get; init; } = DefaultSubagentBackgroundWorkTimeout;

    /// <summary>
    /// The default value of <see cref="SubagentBackgroundWorkTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultSubagentBackgroundWorkTimeout = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Gets or sets how long past its own timeout the run keeps waiting for background work the main agent armed and then
    /// stopped to wait for: a <c>Monitor</c> (<c>timeout_ms</c>) or a <c>Bash</c> command run in the background with an
    /// explicit <c>timeout</c>. The agent is woken by their events or exit, so ending the run at the turn's <c>result</c>
    /// kills them and loses everything they would report. The CLI ends such work when its timeout passes and reports it,
    /// so the run is open for the declared timeout plus this grace period; a background command without a timeout, such
    /// as a dev server, is never awaited. When the time passes the run ends with the results already received.
    /// The default is one minute and the value must be positive.
    /// </summary>
    public TimeSpan BackgroundWorkGracePeriod { get; init; } = DefaultBackgroundWorkGracePeriod;

    /// <summary>
    /// The default value of <see cref="BackgroundWorkGracePeriod" />.
    /// </summary>
    public static readonly TimeSpan DefaultBackgroundWorkGracePeriod = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets how long the run waits for a prompt written to the CLI to start its own turn after an earlier turn
    /// has ended. The CLI can end a different turn first, such as the injected turn for a stopped background-task
    /// notification on resume. When this wait passes the prompt is sent once more; a second pass ends the run with the
    /// results already received. The default is 20 seconds and the value must be positive.
    /// </summary>
    public TimeSpan PromptStartTimeout { get; init; } = DefaultPromptStartTimeout;

    /// <summary>
    /// The default value of <see cref="PromptStartTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultPromptStartTimeout = TimeSpan.FromSeconds(20);
}