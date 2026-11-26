namespace HagiCode.Libs.Providers.Antigravity;

/// <summary>The result of asking a running Antigravity execution to take a follow-up message.</summary>
public enum AntigravitySteerResult
{
    /// <summary>The follow-up was sent as a prompt on the running session, so its outcome will be reported.</summary>
    Sent,

    /// <summary>The execution cannot take the steer now: no prompt has started, the run has ended, or the text is empty.</summary>
    NotSteerable
}

/// <summary>The eventual result of one accepted Antigravity steer.</summary>
public enum AntigravitySteerOutcome
{
    /// <summary>The steered prompt completed without a failure, so the run took the follow-up as its next turn.</summary>
    Consumed,

    /// <summary>The steered prompt failed, was cancelled, or the run ended before it completed.</summary>
    NotConsumed
}

/// <summary>Outcome data for one accepted Antigravity steer.</summary>
public sealed class AntigravitySteerOutcomeEventArgs(string steerId, AntigravitySteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the caller-provided steer identity.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether the run consumed the steer.</summary>
    public AntigravitySteerOutcome Outcome { get; } = outcome;
}

/// <summary>Per-execution channel for steering a running Antigravity session over ACP.</summary>
/// <remarks>
/// Create a new channel for each execution. The provider attaches it once the first prompt has been sent; before that, and after
/// the run ends, <see cref="SteerAsync"/> returns <see cref="AntigravitySteerResult.NotSteerable"/> and writes nothing.
/// <para>
/// ACP has no interrupt. A steer is a second <c>session/prompt</c> on the same session. Antigravity ends the first prompt at that
/// point and runs the steered text as the next turn, so the run keeps going until that prompt completes. The outcome is
/// therefore known only when the steered prompt finishes: <see cref="AntigravitySteerOutcome.Consumed"/> then, or
/// <see cref="AntigravitySteerOutcome.NotConsumed"/> when it fails or the run ends first.
/// </para>
/// </remarks>
public sealed class AntigravityRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<AntigravitySteerResult>>? _sink;

    /// <summary>Raised once for every accepted steer when its outcome is resolved.</summary>
    public event EventHandler<AntigravitySteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>Sends follow-up text into the running session as a new prompt.</summary>
    /// <returns><see cref="AntigravitySteerResult.NotSteerable"/> for empty text, which is never written.</returns>
    public Task<AntigravitySteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentNullException.ThrowIfNull(text);

        Func<string, string, CancellationToken, Task<AntigravitySteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        if (sink is null || string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(AntigravitySteerResult.NotSteerable);
        }

        return sink(steerId, text, cancellationToken);
    }

    internal void Attach(Func<string, string, CancellationToken, Task<AntigravitySteerResult>> sink)
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

    internal void Report(string steerId, AntigravitySteerOutcome outcome)
    {
        OutcomeObserved?.Invoke(this, new AntigravitySteerOutcomeEventArgs(steerId, outcome));
    }
}
