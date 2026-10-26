namespace HagiCode.Libs.Providers.Pi;

/// <summary>The result of asking a running Pi execution to take a follow-up message.</summary>
public enum PiSteerResult
{
    /// <summary>Pi acknowledged the steer as queued (or handled), so the steer is accepted and its outcome will be reported.</summary>
    Sent,

    /// <summary>The execution cannot take the steer now: it is not in RPC mode, is not running a prompt, has ended, refused the steer, or the text is empty.</summary>
    NotSteerable
}

/// <summary>The eventual result of one accepted Pi steer.</summary>
public enum PiSteerOutcome
{
    /// <summary>Pi's event stream shows the steered message was injected into the run.</summary>
    Consumed,

    /// <summary>The run ended, was cancelled, or Pi settled before the steer was injected.</summary>
    NotConsumed
}

/// <summary>Outcome data for one accepted Pi steer.</summary>
public sealed class PiSteerOutcomeEventArgs(string steerId, PiSteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the caller-provided steer identity.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether the run consumed the steer.</summary>
    public PiSteerOutcome Outcome { get; } = outcome;
}

/// <summary>Per-execution channel for steering a Pi run that uses RPC mode.</summary>
/// <remarks>
/// Create a new channel for each execution. The provider attaches it only while the RPC run is iterated and Pi has started
/// the prompt; outside that window <see cref="SteerAsync"/> returns <see cref="PiSteerResult.NotSteerable"/> and writes nothing.
/// A run in JSON print mode never attaches it. A steer is accepted once Pi acknowledges it as queued, and its outcome is then
/// reported through <see cref="OutcomeObserved"/> exactly once: <see cref="PiSteerOutcome.Consumed"/> when Pi injects the
/// message into the run, <see cref="PiSteerOutcome.NotConsumed"/> when the run ends, is cancelled or Pi exits first.
/// </remarks>
public sealed class PiRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<PiSteerResult>>? _sink;

    /// <summary>Raised once for every accepted steer when its outcome is resolved.</summary>
    public event EventHandler<PiSteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>Asks the attached execution to deliver follow-up text to the running prompt.</summary>
    /// <returns>
    /// <see cref="PiSteerResult.NotSteerable"/> for an empty or whitespace-only text, which Pi would accept and run as an
    /// empty turn, so it is never written.
    /// </returns>
    public Task<PiSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentNullException.ThrowIfNull(text);

        Func<string, string, CancellationToken, Task<PiSteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        if (sink is null || string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(PiSteerResult.NotSteerable);
        }

        return sink(steerId, text, cancellationToken);
    }

    internal void Attach(Func<string, string, CancellationToken, Task<PiSteerResult>> sink)
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

    internal void Report(string steerId, PiSteerOutcome outcome)
    {
        OutcomeObserved?.Invoke(this, new PiSteerOutcomeEventArgs(steerId, outcome));
    }
}
