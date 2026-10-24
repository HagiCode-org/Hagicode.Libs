namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// Describes a one-shot Pi CLI invocation.
/// </summary>
public sealed record PiOptions
{
    /// <summary>
    /// Gets or sets the custom Pi executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory for the Pi process.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the Pi provider name.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Gets or sets the Pi model selector.
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
    /// Gets or sets the explicit Pi session identifier.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets or sets the Pi session directory.
    /// </summary>
    public string? SessionDirectory { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether session persistence should be disabled.
    /// </summary>
    public bool NoSession { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether all Pi tools should be disabled.
    /// </summary>
    public bool DisableAllTools { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether only built-in Pi tools should be disabled.
    /// </summary>
    public bool DisableBuiltinTools { get; init; }

    /// <summary>
    /// Gets or sets the Pi tool allowlist.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the Pi tool denylist.
    /// </summary>
    public IReadOnlyList<string> ExcludedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets skill files or directories Pi loads for this run, each sent as its own <c>--skill &lt;path&gt;</c>.
    /// </summary>
    public IReadOnlyList<string> SkillPaths { get; init; } = [];

    /// <summary>
    /// Gets or sets the Pi thinking level.
    /// </summary>
    public string? Thinking { get; init; }

    /// <summary>
    /// Gets or sets environment variable overrides for the Pi process.
    /// A <see langword="null" /> value removes the variable from the child process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional Pi CLI arguments appended after structured flags.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the callback that answers the interactive questions the agent asks through the bundled
    /// <c>ask_user_question</c> extension tool. It is <see langword="null" /> by default, which keeps the provider exactly as
    /// it was: Pi runs in JSON print mode with no extension.
    /// Setting it (and leaving <see cref="DisableAllTools" /> off) starts Pi in RPC mode with the bundled extension loaded,
    /// which makes Pi offer <c>ask_user_question</c> to the agent, and makes the provider answer the extension's dialog
    /// requests.
    /// </summary>
    /// <remarks>
    /// The handler runs as a background task per question, so the stream keeps flowing while a user thinks, and it receives a
    /// token that is cancelled when <see cref="QuestionTimeout" /> passes or the run ends. Returning
    /// <see langword="null" />, timing out, or throwing declines the question. Every other dialog Pi raises (a user
    /// extension's <c>select</c>, <c>confirm</c> or <c>editor</c>, an <c>input</c> without the bundled marker) is answered as
    /// cancelled and never reaches the handler. An interactive request is also yielded to the caller as an
    /// <c>extension_ui_request</c> stream message in the position it arrived (see
    /// <see cref="PiUserInputRequest.TryFromCliMessage" />).
    /// </remarks>
    public Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>>? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets how long an interactive question may stay unanswered. When it passes, the question is declined and the
    /// run goes on to its normal result.
    /// The default is 30 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> to wait without a limit.
    /// A value that is neither positive nor infinite is rejected with an <see cref="ArgumentException" /> before any process
    /// starts, when <see cref="UserInputHandler" /> is set.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>
    /// The default value of <see cref="QuestionTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);
}
