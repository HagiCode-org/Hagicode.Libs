namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// Describes an OpenCode runtime/session invocation.
/// </summary>
public sealed record OpenCodeOptions
{
    /// <summary>
    /// Gets or sets the custom OpenCode executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the externally managed OpenCode base URL.
    /// When supplied, the provider attaches instead of launching a local runtime.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets or sets the OpenCode server password for an attached runtime (<see cref="BaseUrl"/>).
    /// Owned runtimes capture their own password and ignore this value.
    /// </summary>
    public string? ServerPassword { get; init; }

    /// <summary>
    /// Gets or sets the working directory forwarded to the OpenCode HTTP API.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the optional OpenCode workspace identifier.
    /// </summary>
    public string? Workspace { get; init; }

    /// <summary>
    /// Gets or sets the optional model override.
    /// Supports either <c>&lt;provider&gt;/&lt;model&gt;</c> or slashless values.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to resume.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or sets the optional session title used when a new session is created.
    /// </summary>
    public string? SessionTitle { get; init; }

    /// <summary>
    /// Gets or sets the runtime startup timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets the HTTP request timeout.
    /// </summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into a launched OpenCode runtime.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets skill directories added to <c>skills.paths</c> of the inline configuration
    /// (<c>OPENCODE_CONFIG_CONTENT</c>) of a launched runtime. A value already in
    /// <see cref="EnvironmentVariables" />, or else in this process's environment, is extended, never replaced.
    /// An attached runtime (<see cref="BaseUrl" />) reads no environment of ours, so it ignores this.
    /// </summary>
    public IReadOnlyList<string> SkillPaths { get; init; } = [];

    /// <summary>
    /// Gets or sets extra arguments appended to <c>opencode serve</c>.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the callback that answers the questions the agent asks through OpenCode's built-in <c>question</c> tool.
    /// It is <see langword="null" /> by default. A run without a handler keeps the provider's behaviour, except that a question
    /// is declined at once (with a message the agent receives) instead of being left pending until the request timeout.
    /// </summary>
    /// <remarks>
    /// The handler runs as a background task per question, so the event stream keeps flowing while a user thinks. It receives a
    /// token that is cancelled when <see cref="QuestionTimeout"/> passes, the run ends, or the caller cancels. Returning
    /// <see langword="null"/>, timing out, or throwing declines the question. A value that is not one of the offered labels is
    /// sent unchanged. The wait for an answer does not count against <see cref="RequestTimeout"/>.
    /// </remarks>
    public Func<OpenCodeUserInputRequest, CancellationToken, Task<OpenCodeUserInputResponse?>>? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets how long an interactive question may stay unanswered. When it passes, the question is declined and the run
    /// goes on to its normal result. The default is 30 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to
    /// wait without a limit. A value that is neither positive nor infinite is rejected with an <see cref="ArgumentException"/>
    /// before any request is sent, when <see cref="UserInputHandler"/> is set.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>
    /// The default value of <see cref="QuestionTimeout"/>.
    /// </summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets the handle that accepts follow-up text into this run's active turn. It is <see langword="null"/> by default, and
    /// the run then behaves as before. Create a new handle for each run.
    /// </summary>
    public OpenCodeRunSteering? Steering { get; init; }

    /// <summary>
    /// Gets or sets how long an accepted steer may wait for OpenCode to report it delivered before it is reported as not consumed.
    /// The default is 5 minutes, which covers a running shell tool call, because OpenCode delivers a steer at the step boundary after
    /// the tool finishes. A value that is not positive and finite is rejected with an <see cref="ArgumentOutOfRangeException"/> before
    /// any request is sent, when <see cref="Steering"/> is set.
    /// </summary>
    public TimeSpan SteerDeliveryTimeout { get; init; } = DefaultSteerDeliveryTimeout;

    /// <summary>
    /// The default value of <see cref="SteerDeliveryTimeout"/>.
    /// </summary>
    public static readonly TimeSpan DefaultSteerDeliveryTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets whether the provider reports the agent work of the run (child sessions the agent spawned) as
    /// <c>agent_work_snapshot</c> messages. The default is <see langword="true"/>. When <see langword="false"/>, child sessions are
    /// not tracked, no snapshot is produced, and a turn ends as soon as the prompted session reports its result.
    /// </summary>
    public bool EmitAgentWorkSnapshots { get; init; } = true;

    /// <summary>
    /// Gets or sets how long a turn stays open after OpenCode reports success while a tracked child session is still active.
    /// The turn then keeps reading so that a follow-up turn of the prompted session and the end of the child are observed. The
    /// default is 10 minutes. When it passes, the still-active items are reported as <c>stopped</c>. Use
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> to wait without a limit. A value that is neither positive nor
    /// infinite is rejected with an <see cref="ArgumentException"/> before any request is sent.
    /// </summary>
    public TimeSpan AgentWorkWaitTimeout { get; init; } = DefaultAgentWorkWaitTimeout;

    /// <summary>
    /// Gets or sets how long the turn waits, after the last tracked child has ended and the prompted session is idle, for a follow-up
    /// turn of the prompted session to start before it returns. The default is 2 seconds. Zero returns at once. A negative or infinite
    /// value is rejected with an <see cref="ArgumentException"/> before any request is sent.
    /// </summary>
    public TimeSpan AgentWorkSettleInterval { get; init; } = DefaultAgentWorkSettleInterval;

    /// <summary>
    /// The default value of <see cref="AgentWorkWaitTimeout"/>.
    /// </summary>
    public static readonly TimeSpan DefaultAgentWorkWaitTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The default value of <see cref="AgentWorkSettleInterval"/>.
    /// </summary>
    public static readonly TimeSpan DefaultAgentWorkSettleInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Formats the options without exposing <see cref="ServerPassword"/>.
    /// </summary>
    public override string ToString()
    {
        return $"OpenCodeOptions {{ ExecutablePath = {ExecutablePath}, BaseUrl = {BaseUrl}, WorkingDirectory = {WorkingDirectory}, Workspace = {Workspace}, Model = {Model}, SessionId = {SessionId}, ServerPassword = {(string.IsNullOrEmpty(ServerPassword) ? "null" : OpenCodeSecretRedactor.Marker)}, UserInputHandler = {(UserInputHandler is null ? "null" : "set")}, QuestionTimeout = {QuestionTimeout} }}";
    }
}
