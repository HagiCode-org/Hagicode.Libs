namespace HagiCode.Libs.Providers.Omp;

/// <summary>The result of asking a running OMP execution to take a follow-up message.</summary>
public enum OmpSteerResult
{
    /// <summary>OMP acknowledged the steer, so the steer is accepted and its outcome will be reported.</summary>
    Sent,

    /// <summary>The execution cannot take the steer now: it is not in RPC mode, is not running a prompt, has ended, refused the steer, or the text is empty.</summary>
    NotSteerable
}

/// <summary>The eventual result of one accepted OMP steer.</summary>
public enum OmpSteerOutcome
{
    /// <summary>OMP's event stream shows the steered message entering the run.</summary>
    Consumed,

    /// <summary>The run ended, was cancelled, or OMP exited before the steer entered the run.</summary>
    NotConsumed
}

/// <summary>Outcome data for one accepted OMP steer.</summary>
public sealed class OmpSteerOutcomeEventArgs(string steerId, OmpSteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the caller-provided steer identity.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether the run consumed the steer.</summary>
    public OmpSteerOutcome Outcome { get; } = outcome;
}

/// <summary>Per-execution channel for steering an OMP run that uses RPC mode.</summary>
/// <remarks>
/// Create a new channel for each execution. The provider attaches it only while the RPC run is iterated and OMP has started
/// the agent; outside that window <see cref="SteerAsync"/> returns <see cref="OmpSteerResult.NotSteerable"/> and writes nothing.
/// The window closes at the first <c>agent_end</c> of the run. A run in JSON print mode never attaches it. A steer is accepted
/// once OMP answers the <c>steer</c> command with success, and its outcome is then reported through
/// <see cref="OutcomeObserved"/> exactly once: <see cref="OmpSteerOutcome.Consumed"/> when OMP's output shows the message entering
/// the run, <see cref="OmpSteerOutcome.NotConsumed"/> when the run ends, is cancelled or OMP exits first.
/// </remarks>
public sealed class OmpRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<OmpSteerResult>>? _sink;

    /// <summary>Raised once for every accepted steer when its outcome is resolved.</summary>
    public event EventHandler<OmpSteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>Asks the attached execution to deliver follow-up text to the running prompt.</summary>
    /// <returns>
    /// <see cref="OmpSteerResult.NotSteerable"/> for an empty or whitespace-only text, which OMP would accept and drop without
    /// running it, so it is never written.
    /// </returns>
    public Task<OmpSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentNullException.ThrowIfNull(text);

        Func<string, string, CancellationToken, Task<OmpSteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        if (sink is null || string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(OmpSteerResult.NotSteerable);
        }

        return sink(steerId, text, cancellationToken);
    }

    internal void Attach(Func<string, string, CancellationToken, Task<OmpSteerResult>> sink)
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

    internal void Report(string steerId, OmpSteerOutcome outcome)
    {
        OutcomeObserved?.Invoke(this, new OmpSteerOutcomeEventArgs(steerId, outcome));
    }
}
