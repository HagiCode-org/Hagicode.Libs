namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// The result of asking a running Claude Code execution to take a follow-up message.
/// </summary>
public enum ClaudeSteerResult
{
    /// <summary>The message was written to the CLI. Whether the CLI consumed it is reported later as an outcome.</summary>
    Sent,

    /// <summary>The message was not written: the execution is not connected yet, has ended, or refused it.</summary>
    NotSteerable
}

/// <summary>
/// What became of a steer the CLI accepted. Every accepted steer is reported exactly once.
/// </summary>
public enum ClaudeSteerOutcome
{
    /// <summary>The CLI echoed the message back, so it was injected into the run.</summary>
    Consumed,

    /// <summary>The run ended, or was cancelled, before the CLI echoed the message. The caller owns the message from here.</summary>
    NotConsumed
}

/// <summary>
/// Carries the outcome of one accepted steer, as reported by <see cref="ClaudeRunSteering.OutcomeObserved" />.
/// </summary>
/// <param name="steerId">The id the caller gave the steer.</param>
/// <param name="outcome">Whether the CLI consumed the steer.</param>
public sealed class ClaudeSteerOutcomeEventArgs(string steerId, ClaudeSteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the id the caller gave the steer.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether the CLI consumed the steer.</summary>
    public ClaudeSteerOutcome Outcome { get; } = outcome;
}

/// <summary>
/// Injects follow-up user messages into one running Claude Code execution.
/// </summary>
/// <remarks>
/// Create one handle per execution and pass it in <see cref="ClaudeCodeOptions.Steering" />. The provider attaches the handle
/// once the CLI is connected and detaches it when the execution ends. While it is detached, <see cref="SteerAsync" />
/// returns <see cref="ClaudeSteerResult.NotSteerable" /> and writes nothing.
/// <para>
/// A steer is written as a <c>user</c> message with priority <c>next</c>, so the CLI injects it into the current turn at the
/// next tool boundary. Whether that happened is not known when <see cref="SteerAsync" /> returns: the CLI echoes the message,
/// and the echo is reported through <see cref="OutcomeObserved" /> as <see cref="ClaudeSteerOutcome.Consumed" />. A steer that
/// the CLI never echoes is reported as <see cref="ClaudeSteerOutcome.NotConsumed" /> no later than the end of the execution,
/// which includes timeouts, cancellation and a CLI that exits. A steer still pending when the execution ends keeps it open
/// for at most <see cref="ClaudeCodeOptions.SteerDeliveryTimeout" />.
/// </para>
/// <para>
/// The event is raised on the thread that reads the CLI output. Handlers should only record the outcome and return.
/// </para>
/// </remarks>
public sealed class ClaudeRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<ClaudeSteerResult>>? _sink;

    /// <summary>
    /// Raised once for every accepted steer, with its outcome. Consumed steers are reported when the CLI echoes them;
    /// steers still unresolved when the execution ends are reported as not consumed.
    /// </summary>
    public event EventHandler<ClaudeSteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>
    /// Asks the running execution to take a follow-up user message.
    /// </summary>
    /// <param name="steerId">A caller-chosen id that is unique among the steers of this execution. It becomes the message <c>uuid</c>.</param>
    /// <param name="text">The text of the follow-up message.</param>
    /// <param name="cancellationToken">Cancels the write. A steer that is already written is not withdrawn.</param>
    /// <returns>
    /// <see cref="ClaudeSteerResult.Sent" /> when the message was written; <see cref="ClaudeSteerResult.NotSteerable" /> when the
    /// execution is not attached, is complete, already has a steer with that id, or the write failed.
    /// </returns>
    public Task<ClaudeSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Func<string, string, CancellationToken, Task<ClaudeSteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        return sink is null
            ? Task.FromResult(ClaudeSteerResult.NotSteerable)
            : sink(steerId, text, cancellationToken);
    }

    /// <summary>
    /// Connects the handle to a running execution. Called by the provider once the CLI is connected.
    /// </summary>
    internal void Attach(Func<string, string, CancellationToken, Task<ClaudeSteerResult>> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sink = sink;
        }
    }

    /// <summary>
    /// Disconnects the handle. Called by the provider when the execution ends.
    /// </summary>
    internal void Detach()
    {
        lock (_gate)
        {
            _sink = null;
        }
    }

    /// <summary>
    /// Reports the outcome of one accepted steer.
    /// </summary>
    internal void Report(ClaudeSteerResolution resolution)
    {
        OutcomeObserved?.Invoke(this, new ClaudeSteerOutcomeEventArgs(resolution.SteerId, resolution.Outcome));
    }
}

/// <summary>
/// The resolution of one accepted steer, produced by the completion tracker and reported through the steering handle.
/// </summary>
/// <param name="SteerId">The id the caller gave the steer.</param>
/// <param name="Outcome">Whether the CLI consumed the steer.</param>
internal sealed record ClaudeSteerResolution(string SteerId, ClaudeSteerOutcome Outcome);
