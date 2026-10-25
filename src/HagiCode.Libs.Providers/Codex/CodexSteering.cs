namespace HagiCode.Libs.Providers.Codex;

/// <summary>The result of asking a running Codex execution to take a follow-up message.</summary>
public enum CodexSteerResult
{
    /// <summary>The interactive transport wrote the steer request and accepted responsibility for resolving its outcome.</summary>
    Sent,

    /// <summary>The execution cannot accept a steer now, including when it uses the exec transport.</summary>
    NotSteerable
}

/// <summary>The eventual result of one accepted Codex steer.</summary>
public enum CodexSteerOutcome
{
    /// <summary>The app-server positively acknowledged the message for the bound thread and active turn.</summary>
    Consumed,

    /// <summary>The run ended, delivery was refused, or confirmation did not arrive before the configured deadline.</summary>
    NotConsumed
}

/// <summary>Outcome data for one accepted Codex steer.</summary>
public sealed class CodexSteerOutcomeEventArgs(string steerId, CodexSteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the caller-provided steer identity.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether the runtime confirmed consumption.</summary>
    public CodexSteerOutcome Outcome { get; } = outcome;
}

/// <summary>Per-execution channel for steering a supported Codex app-server turn.</summary>
/// <remarks>
/// Create a new channel for each execution. The provider attaches it only when Codex CLI meets the interactive minimum version
/// and the app-server handshake succeeds; otherwise it returns <see cref="CodexSteerResult.NotSteerable"/>. Steering uses the
/// app-server's native active-turn operation and does not enable agent questions. A successful request write is reported as
/// <see cref="CodexSteerResult.Sent"/>, not as delivery confirmation. The app-server's acknowledgement must match the original
/// thread and turn to report <see cref="CodexSteerOutcome.Consumed"/> through <see cref="OutcomeObserved"/>. The finite
/// acknowledgement deadline defaults to <see cref="CodexSessionOptions.DefaultSteerDeliveryTimeout"/>; timeout, turn failure,
/// cancellation, or transport teardown reports <see cref="CodexSteerOutcome.NotConsumed"/>.
/// </remarks>
public sealed class CodexRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<CodexSteerResult>>? _sink;

    /// <summary>Raised once for every accepted steer when its acknowledgement or another terminal outcome is resolved.</summary>
    public event EventHandler<CodexSteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>Asks the attached execution to deliver follow-up text to its current active turn.</summary>
    public Task<CodexSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Func<string, string, CancellationToken, Task<CodexSteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        return sink is null
            ? Task.FromResult(CodexSteerResult.NotSteerable)
            : sink(steerId, text, cancellationToken);
    }

    internal void Attach(Func<string, string, CancellationToken, Task<CodexSteerResult>> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sink = sink;
        }
    }

    internal void Detach()
    {
        lock (_gate)
        {
            _sink = null;
        }
    }

    internal void Report(string steerId, CodexSteerOutcome outcome)
    {
        OutcomeObserved?.Invoke(this, new CodexSteerOutcomeEventArgs(steerId, outcome));
    }
}
