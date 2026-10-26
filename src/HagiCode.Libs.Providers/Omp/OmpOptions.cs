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
    /// Gets or sets plugin directories OMP loads for this run, each sent as its own <c>--plugin-dir &lt;path&gt;</c>.
    /// </summary>
    public IReadOnlyList<string> PluginDirectories { get; init; } = [];

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

    /// <summary>
    /// Gets or sets the callback that answers the interactive questions the agent asks through OMP's built-in <c>ask</c>
    /// tool. It is <see langword="null" /> by default, which keeps the provider exactly as it was: OMP runs in JSON print
    /// mode.
    /// Setting it (and leaving <see cref="DisableAllTools" /> off) starts OMP with <c>--mode rpc-ui</c>, enables OMP's
    /// structured ask dialog before the prompt is sent, and makes the provider answer each <c>ask</c> request with what the
    /// handler returns. An OMP build that rejects the ask dialog runs the request in JSON print mode instead.
    /// </summary>
    /// <remarks>
    /// The handler runs as a background task per question, so the stream keeps flowing while a user thinks, and it receives a
    /// token that is cancelled when <see cref="QuestionTimeout" /> passes, when OMP closes the question, or when the run ends.
    /// Returning <see langword="null" />, leaving a question unanswered, timing out or throwing declines the request; OMP then
    /// ends the run as aborted, which the provider reports as a normal end of run (unlike Claude and Pi, the agent does not
    /// continue). Every other dialog OMP raises (an extension's <c>select</c>, <c>confirm</c>, <c>input</c> or
    /// <c>editor</c>) is answered as cancelled and never reaches the handler. An interactive request is also yielded to the
    /// caller as an <c>extension_ui_request</c> stream message in the position it arrived (see
    /// <see cref="OmpUserInputRequest.TryFromCliMessage" />).
    /// </remarks>
    public Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>>? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets how long an interactive question may stay unanswered. When it passes, the question is declined and OMP
    /// ends the run as aborted. OMP's own ask timeout is not used.
    /// The default is 30 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> to wait without a limit.
    /// A value that is neither positive nor infinite is rejected with an <see cref="ArgumentException" /> before any process
    /// starts, when <see cref="UserInputHandler" /> is set.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>
    /// The default value of <see cref="QuestionTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets the channel that steers the run: a follow-up user message sent to the same OMP process while it runs.
    /// It is <see langword="null" /> by default, and it takes effect only for a run in <c>rpc-ui</c> mode (see
    /// <see cref="UserInputHandler" />); a JSON print run never attaches it, so every steer on it is not steerable.
    /// </summary>
    /// <remarks>
    /// Create a new <see cref="OmpRunSteering" /> for each execution. Its outcomes arrive through
    /// <see cref="OmpRunSteering.OutcomeObserved" /> while the run is iterated.
    /// </remarks>
    public OmpRunSteering? Steering { get; init; }

    /// <summary>
    /// Gets or sets how long a steer may wait for OMP's reply to the <c>steer</c> command. A reply that does not come in time
    /// makes the steer not steerable. It does not bound consumption: a steer OMP holds is waited for until OMP takes it into the
    /// run or the run ends. The default is 30 seconds. A value that is not positive is rejected with an
    /// <see cref="ArgumentOutOfRangeException" /> before any process starts, when <see cref="Steering" /> is set on an
    /// <c>rpc-ui</c> run.
    /// </summary>
    public TimeSpan SteerDeliveryTimeout { get; init; } = DefaultSteerDeliveryTimeout;

    /// <summary>
    /// The default value of <see cref="SteerDeliveryTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultSteerDeliveryTimeout = TimeSpan.FromSeconds(30);
}
