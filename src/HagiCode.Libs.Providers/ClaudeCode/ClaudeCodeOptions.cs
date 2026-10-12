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
    /// so the run is open for the declared timeout plus this grace period; a background command without a timeout is
    /// bounded by <see cref="UntimedBackgroundCommandTimeout" /> instead. When the time passes the run ends with the
    /// results already received. The default is one minute and the value must be positive.
    /// </summary>
    public TimeSpan BackgroundWorkGracePeriod { get; init; } = DefaultBackgroundWorkGracePeriod;

    /// <summary>
    /// The default value of <see cref="BackgroundWorkGracePeriod" />.
    /// </summary>
    public static readonly TimeSpan DefaultBackgroundWorkGracePeriod = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets how long the run stays open after a <c>result</c> while a <c>Bash</c> command the main agent started in
    /// the background with no <c>timeout</c> is still running, counted from when the command started. An agent that starts
    /// a build this way and ends its turn to wait for it is woken by the command's exit; ending the run at that
    /// <c>result</c> kills the CLI and the command's notification, the agent's wait is lost, and the next message finds the
    /// command stopped or still running as an orphan. The CLI has no limit of its own for such a command, so the bound is
    /// the host's. A command that is meant to run for good, such as a dev server, keeps the run open until the limit
    /// passes or the run is stopped; then the run ends with the results already received.
    /// The default is 30 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> to wait without a limit, or
    /// <see cref="TimeSpan.Zero" /> to never wait for a background command that declared no timeout.
    /// </summary>
    public TimeSpan UntimedBackgroundCommandTimeout { get; init; } = DefaultUntimedBackgroundCommandTimeout;

    /// <summary>
    /// The default value of <see cref="UntimedBackgroundCommandTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultUntimedBackgroundCommandTimeout = TimeSpan.FromMinutes(30);

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

    /// <summary>
    /// Gets or sets how long the run waits, after a turn's <c>result</c>, for the turn that handles a task notification
    /// (a subagent or background command that finished) the CLI queued for the main agent. A subagent that finishes while
    /// the main agent is still writing its final answer is reported mid-turn but can only be handed to the agent by a new
    /// turn, which the CLI starts within milliseconds of the <c>result</c>; ending the run first kills the CLI and the
    /// agent never sees what the subagent reported. The wait ends as soon as that turn starts, so the bound only matters
    /// for a notification the CLI handled without a visible sign. The default is 10 seconds and the value must be positive.
    /// </summary>
    public TimeSpan NotificationTurnTimeout { get; init; } = DefaultNotificationTurnTimeout;

    /// <summary>
    /// The default value of <see cref="NotificationTurnTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultNotificationTurnTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the callback that answers the interactive questions the agent asks through the CLI's
    /// <c>AskUserQuestion</c> tool. It is <see langword="null" /> by default, which keeps the provider exactly as it was.
    /// Setting it adds <c>--permission-prompt-tool stdio</c> to the CLI arguments, which makes the CLI offer
    /// <c>AskUserQuestion</c> to the agent, and makes the provider answer the CLI's <c>control_request</c> messages.
    /// </summary>
    /// <remarks>
    /// The handler runs as a background task per question, so the stream keeps flowing while a user thinks, and it receives a
    /// token that is cancelled when <see cref="QuestionTimeout" /> passes or the run ends. Returning
    /// <see langword="null" />, timing out, or throwing denies the question. Every other tool permission request is
    /// denied with a fixed message and never reaches the handler: the CLI refuses the tools its permission mode does not
    /// approve on its own, and the provider keeps that outcome. An interactive request is also yielded to the caller as a
    /// <c>control_request</c> stream message in the position it arrived (see <see cref="ClaudeUserInputRequest.TryFromCliMessage" />).
    /// </remarks>
    public Func<ClaudeUserInputRequest, CancellationToken, Task<ClaudeUserInputResponse?>>? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets how long an interactive question may stay unanswered. While a question is pending the other wait
    /// bounds do not end the run; this is the only bound. When it passes, the question is denied with a message saying that
    /// no answer was received and the run goes on to its normal result.
    /// The default is 30 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> to wait without a limit.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>
    /// The default value of <see cref="QuestionTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);
}
