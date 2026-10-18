namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// Describes a GitHub Copilot CLI invocation routed through the SDK-managed session path.
/// </summary>
public sealed record CopilotOptions
{
    /// <summary>
    /// Default request timeout for Copilot prompt execution.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Default idle timeout before a stalled Copilot turn is aborted.
    /// </summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Default startup timeout used while booting the Copilot SDK session.
    /// </summary>
    public static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets the custom Copilot executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the Copilot model override.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the working directory used for SDK session creation.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the provider-native Copilot session identifier to resume or pin.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether provider-native session reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets the request timeout used for one Copilot prompt.
    /// </summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>
    /// Gets or sets the idle timeout used for one Copilot prompt when no session events arrive.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>
    /// Gets or sets the startup timeout used while creating the SDK session.
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = DefaultStartupTimeout;

    /// <summary>
    /// Gets or sets the Copilot authentication source.
    /// </summary>
    public CopilotAuthSource AuthSource { get; init; } = CopilotAuthSource.LoggedInUser;

    /// <summary>
    /// Gets or sets the GitHub token used when <see cref="AuthSource" /> is <see cref="CopilotAuthSource.GitHubToken" />.
    /// </summary>
    public string? GitHubToken { get; init; }

    /// <summary>
    /// Gets or sets the optional Copilot CLI URL override.
    /// </summary>
    public string? CliUrl { get; init; }

    /// <summary>
    /// Gets or sets Copilot permission defaults forwarded to verified CLI startup arguments.
    /// Setting all typed allow-all flags enables the maximum-access profile without relying on raw CLI flags.
    /// </summary>
    public CopilotPermissionOptions Permissions { get; init; } = new();

    /// <summary>
    /// Gets or sets environment variables applied while launching the SDK-managed Copilot runtime.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets additional Copilot startup arguments that are filtered through the verified compatibility matrix.
    /// </summary>
    public IReadOnlyList<string> AdditionalArgs { get; init; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether to disable ask-user prompts during execution.
    /// </summary>
    public bool NoAskUser { get; init; } = true;

    /// <summary>
    /// Gets or sets the callback that answers the questions the agent asks through its <c>ask_user</c> tool. It is
    /// <see langword="null" /> by default, which keeps the provider exactly as it was: the derived <c>--no-ask-user</c> flag
    /// stays in effect and the agent cannot ask.
    /// </summary>
    /// <remarks>
    /// Setting it registers the SDK user-input callback for fresh and resumed sessions and stops the provider from passing
    /// <c>--no-ask-user</c>, whatever <see cref="NoAskUser" /> and <see cref="AdditionalArgs" /> say. The handler runs while the
    /// stream keeps flowing, and it receives a token that is cancelled when <see cref="QuestionTimeout" /> passes or the run
    /// ends. Each question has one string choice list and one answer; see <see cref="CopilotUserInputResponse" />. Returning
    /// <see langword="null" />, an answer that is refused, a timeout or a throwing handler declines the question, so Copilot
    /// is told that no answer was given and the run ends with its own result. Tool approvals are not questions: they stay
    /// on <see cref="Permissions" /> and never reach the handler. An interactive question is yielded to the caller as a
    /// <c>question.request</c> stream message in the position it arrived (see <see cref="CopilotUserInputRequest.TryFromCliMessage" />).
    /// </remarks>
    public Func<CopilotUserInputRequest, CancellationToken, Task<CopilotUserInputResponse?>>? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets how long a Copilot question may stay unanswered. While a question is pending the idle timeout and the
    /// request timeout do not end the run; this is the only bound on the wait. When it passes, the question is declined and the
    /// run goes on to its normal result. The default is 30 minutes. Use <see cref="System.Threading.Timeout.InfiniteTimeSpan" />
    /// to wait without a limit. Zero or a negative value is rejected before any SDK call.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>
    /// The default value of <see cref="QuestionTimeout" />.
    /// </summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Formats the options without exposing <see cref="GitHubToken" /> or <see cref="EnvironmentVariables" />.
    /// </summary>
    public override string ToString()
    {
        return $"CopilotOptions {{ ExecutablePath = {ExecutablePath}, Model = {Model}, WorkingDirectory = {WorkingDirectory}, SessionId = {SessionId}, Timeout = {Timeout}, IdleTimeout = {IdleTimeout}, StartupTimeout = {StartupTimeout}, AuthSource = {AuthSource}, GitHubToken = {(string.IsNullOrEmpty(GitHubToken) ? "null" : "set")}, CliUrl = {CliUrl}, NoAskUser = {NoAskUser}, UserInputHandler = {(UserInputHandler is null ? "null" : "set")}, QuestionTimeout = {QuestionTimeout} }}";
    }
}

/// <summary>
/// Defines the Copilot authentication source.
/// </summary>
public enum CopilotAuthSource
{
    /// <summary>
    /// Use the locally logged-in Copilot account.
    /// </summary>
    LoggedInUser = 0,

    /// <summary>
    /// Use an explicit GitHub token supplied in <see cref="CopilotOptions.GitHubToken" />.
    /// </summary>
    GitHubToken = 1
}

/// <summary>
/// Permission settings forwarded to compatible Copilot CLI startup flags.
/// When all typed allow-all switches are enabled, scoped allow/deny lists are treated as suppressed.
/// </summary>
public sealed record CopilotPermissionOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether all tools are allowed.
    /// </summary>
    public bool AllowAllTools { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether all filesystem paths are allowed.
    /// </summary>
    public bool AllowAllPaths { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether all URLs are allowed.
    /// </summary>
    public bool AllowAllUrls { get; init; }

    /// <summary>
    /// Gets or sets the explicitly allowed tool names.
    /// Ignored when the maximum-access allow-all profile is active.
    /// </summary>
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicitly allowed filesystem paths.
    /// Ignored when the maximum-access allow-all profile is active.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicitly denied tool names.
    /// Ignored when the maximum-access allow-all profile is active.
    /// </summary>
    public IReadOnlyList<string> DeniedTools { get; init; } = [];

    /// <summary>
    /// Gets or sets the explicitly denied URLs.
    /// Ignored when the maximum-access allow-all profile is active.
    /// </summary>
    public IReadOnlyList<string> DeniedUrls { get; init; } = [];
}
