using System.Text.Json.Nodes;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Client;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Describes a single SDK-backed Codex session request.
/// </summary>
public sealed record CodexSessionOptions
{
    /// <summary>
    /// Gets or sets the custom Codex executable path.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets or sets the OpenAI API key override.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Gets or sets the OpenAI base URL override.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Gets or sets the thread id to resume.
    /// </summary>
    public string? ThreadId { get; init; }

    /// <summary>
    /// Gets or sets the optional raw Codex config override object.
    /// </summary>
    public JsonObject? Config { get; init; }

    /// <summary>
    /// Gets or sets environment variables injected into the Codex process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> EnvironmentVariables { get; init; } =
        new Dictionary<string, string?>();

    /// <summary>
    /// Gets or sets the per-thread execution options.
    /// </summary>
    public ThreadOptions ThreadOptions { get; init; } = new();

    /// <summary>
    /// Gets or sets the handler that answers the questions the agent asks during the run. Setting it does not by itself
    /// change the transport; <see cref="InteractiveUserInput"/> does.
    /// </summary>
    /// <remarks>
    /// The handler runs as a background task per question, so the event stream keeps flowing while a user thinks. It
    /// receives a token that is cancelled when <see cref="QuestionTimeout"/> passes or the run is stopped. Returning
    /// <see langword="null"/>, timing out or throwing declines the question, and the turn continues.
    /// </remarks>
    public CodexUserInputHandler? UserInputHandler { get; init; }

    /// <summary>
    /// Gets or sets whether agent questions are enabled on the interactive transport (<c>codex app-server</c>). A configured
    /// <see cref="UserInputHandler"/> uses that transport when this is <see langword="true"/>. A configured
    /// <see cref="Steering"/> selects the interactive transport independently. The exec transport is used when neither
    /// interactive feature is requested, or when the CLI version or handshake prevents the interactive path.
    /// </summary>
    public bool InteractiveUserInput { get; init; }

    /// <summary>
    /// Gets the optional steering channel for this execution. Supplying it selects the interactive transport when supported,
    /// independently of whether agent questions are enabled. The app-server must meet the minimum CLI version and complete its
    /// handshake; otherwise the provider uses exec and this channel returns <see cref="CodexSteerResult.NotSteerable"/>.
    /// </summary>
    public CodexRunSteering? Steering { get; init; }

    /// <summary>
    /// Gets or sets how long an interactive question may stay unanswered. When it passes, the question is declined and the run
    /// goes on. The default is 30 minutes. Use <see cref="Timeout.InfiniteTimeSpan"/> to wait without a limit.
    /// </summary>
    public TimeSpan QuestionTimeout { get; init; } = DefaultQuestionTimeout;

    /// <summary>
    /// Gets the finite maximum time to wait for the app-server's correlated acknowledgement of an accepted steer. The default
    /// is 30 seconds. If confirmation does not arrive by this deadline, the steer outcome is reported as not consumed.
    /// </summary>
    public TimeSpan SteerDeliveryTimeout { get; init; } = DefaultSteerDeliveryTimeout;

    /// <summary>
    /// The default value of <see cref="QuestionTimeout"/>.
    /// </summary>
    public static readonly TimeSpan DefaultQuestionTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The default value of <see cref="SteerDeliveryTimeout"/>.</summary>
    public static readonly TimeSpan DefaultSteerDeliveryTimeout = TimeSpan.FromSeconds(30);
}
