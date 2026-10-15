namespace HagiCode.Libs.Providers.Reasonix;

using HagiCode.Libs.Providers;

/// <summary>
/// Describes a Reasonix ACP CLI invocation.
/// </summary>
/// <remarks>
/// Targets the Reasonix 2.x ACP contract (the npm <c>latest</c> line, verified with <c>v2.41.0</c>). The 1.x line is
/// supported on a best-effort basis without dedicated tests. Fields marked legacy are kept so stored settings still
/// load, but Reasonix 2.x does not apply them.
/// </remarks>
public sealed record ReasonixOptions
{
    /// <summary>
    /// Gets or sets the custom Reasonix executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session and filesystem tools.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the Reasonix model ref, a <c>provider</c> or <c>provider/model</c> defined in the user's Reasonix
    /// configuration (for example <c>omniroute/auto/best-coding</c>). It is forwarded as <c>reasonix acp -model &lt;value&gt;</c>.
    /// A ref that the configuration does not define fails session start with a readable error.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to continue. When Reasonix advertises <c>sessionCapabilities.resume</c>, the
    /// session is restored with <c>session/resume</c> and no history is replayed. Otherwise <c>session/load</c> is used and
    /// the replayed history is filtered out of the turn output.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether session reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets a legacy reasoning effort level. Reasonix 2.x ACP has no effort bootstrap flag or config option,
    /// so this value is accepted and not applied.
    /// </summary>
    public string? Effort { get; init; }

    /// <summary>
    /// Gets or sets a legacy session budget in USD. Reasonix 2.x ACP has no budget bootstrap flag or config option,
    /// so this value is accepted and not applied.
    /// </summary>
    public decimal? BudgetUsd { get; init; }

    /// <summary>
    /// Gets or sets a legacy transcript output path. Reasonix 2.x manages its own transcript and reports its path in
    /// prompt results, so this value is accepted and not applied.
    /// </summary>
    public string? TranscriptPath { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether tool approval should be set to <c>yolo</c>. After session start, the
    /// provider calls <c>session/set_config_option</c> with <c>tool_approval=yolo</c> when the session advertises that value.
    /// If the option is missing or the call fails, the default approval mode applies and the turn still runs.
    /// </summary>
    public bool EnableYolo { get; init; }

    /// <summary>
    /// Gets or sets legacy MCP server specifications that used to be forwarded at startup. Reasonix 2.x ACP accepts no MCP
    /// bootstrap flags, so this value is not applied. Configure MCP servers in the Reasonix configuration instead.
    /// </summary>
    public IReadOnlyList<string> McpServerSpecs { get; init; } = [];

    /// <summary>
    /// Gets or sets a legacy MCP tool-name prefix. Reasonix 2.x ACP accepts no MCP bootstrap flags, so this value is not applied.
    /// </summary>
    public string? McpPrefix { get; init; }

    /// <summary>
    /// Gets or sets the ACP bootstrap timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the Reasonix process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the ACP subcommand. Known legacy startup flags (for example
    /// <c>--effort</c>, <c>--budget</c>, <c>--transcript</c>, <c>--mcp</c>, <c>--yolo</c> and <c>--model</c>) are stripped so the
    /// invocation stays valid on the 2.x ACP contract.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the callback that answers the questions the agent asks through Reasonix's built-in <c>ask</c> tool. It is
    /// <see langword="null" /> by default, which keeps the provider as it was: a question request is answered with its first
    /// option, like any permission request, and a warning that names the question is logged.
    /// Setting it makes the provider route each question request (a <c>session/request_permission</c> with a tool call id
    /// <c>ask-…-qN</c> and option ids <c>qN:…</c>) to the handler and answer it with the option whose label the handler
    /// returns. Tool approvals (<c>gate-*</c>) and other permission requests are answered as before.
    /// </summary>
    /// <remarks>
    /// Reasonix sends one request per question, and the next question of the same <c>ask</c> call only after the previous one
    /// is answered, so the handler sees one question at a time. ACP carries one option id per answer: the answer must be
    /// exactly one offered label, even when <see cref="ReasonixUserQuestion.MultiSelect" /> is set, and free text cannot be
    /// sent. Returning <see langword="null" />, an answer that is not exactly one offered label, a timeout or a failing
    /// handler declines the question (<c>qN:cancel</c>, or the <c>cancelled</c> outcome when that option is missing).
    /// Reasonix 2.41.0 then fails the <c>ask</c> call and ends the turn with stop reason <c>error</c>; the provider reports
    /// that turn as <c>terminal.completed</c> with <c>question_declined: true</c> and the original <c>stop_reason</c>.
    /// The handler receives a token that is cancelled when <see cref="QuestionTimeout" /> passes or the run ends. Each
    /// question is also yielded to the caller as a <see cref="ReasonixUserInputRequest.MessageType" /> stream message in the
    /// position it arrived (see <see cref="ReasonixUserInputRequest.TryFromCliMessage" />).
    /// </remarks>
    public Func<ReasonixUserInputRequest, CancellationToken, Task<ReasonixUserInputResponse?>>? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets how long a question may stay unanswered. When it passes, the question is declined.
    /// The default is 30 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> to wait without a limit.
    /// A value that is neither positive nor infinite is rejected with an <see cref="ArgumentException" /> before any process
    /// starts, when <see cref="UserInputHandler" /> is set; without a handler it is ignored.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>
    /// The default value of <see cref="QuestionTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);
}
