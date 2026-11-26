namespace HagiCode.Libs.Providers.Antigravity;

/// <summary>
/// Describes an Antigravity CLI invocation over the Agent Client Protocol (<c>agy acp --stdio</c>).
/// </summary>
public sealed record AntigravityOptions
{
    /// <summary>
    /// Gets or sets the custom Antigravity executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the working directory bound to the ACP session.
    /// </summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Gets or sets the session identifier to resume with <c>session/load</c>.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets a value indicating whether session reuse was requested.
    /// </summary>
    public bool ReuseSession => !string.IsNullOrWhiteSpace(SessionId);

    /// <summary>
    /// Gets or sets the ACP bootstrap timeout.
    /// </summary>
    public TimeSpan? StartupTimeout { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the Antigravity process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets an authentication method identifier to invoke with <c>authenticate</c> before the session starts.
    /// When omitted, the provider never starts an interactive sign-in; it reports a sign-in requirement instead.
    /// </summary>
    public string? AuthenticationMethod { get; init; }

    /// <summary>
    /// Gets or sets a settings file passed as a top-level <c>--settings</c> flag. It must come before <c>acp</c>, because
    /// <c>agy acp</c> does not accept it. Use it to route the CLI through a gateway such as magpie.
    /// </summary>
    public string? SettingsPath { get; init; }

    /// <summary>
    /// Gets or sets directories passed as top-level <c>--add-dir</c> flags before <c>acp</c>. agy loads built-in skills from
    /// <c>&lt;dir&gt;/.agents/skills/&lt;name&gt;/SKILL.md</c> for each added directory, so the caller passes the directory that holds
    /// that layout, not the skill folders themselves. Blank entries are ignored.
    /// </summary>
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];

    /// <summary>
    /// Gets or sets the model passed as a top-level <c>--model</c> flag before <c>acp</c>. Display names and magpie ids are both accepted.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets additional raw CLI arguments appended after the managed <c>acp --stdio</c> arguments.
    /// Tokens are boundary-trimmed individually, whitespace-only tokens are ignored, and duplicate ACP launch arguments are skipped.
    /// </summary>
    public IReadOnlyList<string> ExtraArguments { get; init; } = [];

    /// <summary>
    /// Gets or sets the callback that answers an <c>ask_question</c> question. It runs after the turn that asked has ended, and
    /// its answer is sent as the next prompt on the same session. Returning <see langword="null"/> or throwing declines the question,
    /// and the run ends with that turn. When omitted, questions are still reported as <c>user.question</c> messages and nothing is answered.
    /// </summary>
    public Func<AntigravityUserInputRequest, CancellationToken, Task<AntigravityUserInputResponse?>>? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets how long <see cref="UserInputHandler"/> may wait for an answer before the question is declined.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>The default value of <see cref="QuestionTimeout"/>.</summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Gets or sets the channel for follow-up text sent into the run while it executes. Create a new channel for each execution.
    /// </summary>
    public AntigravityRunSteering? Steering { get; init; }
}
