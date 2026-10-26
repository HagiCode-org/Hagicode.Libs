namespace HagiCode.Libs.Providers.Codebuddy;

/// <summary>The result of asking a running CodeBuddy execution to take a follow-up message.</summary>
public enum CodebuddySteerResult
{
    /// <summary>CodeBuddy acknowledged the steer, so the steer is accepted and its outcome will be reported.</summary>
    Sent,

    /// <summary>The execution cannot take the steer now: no steering transport is attached, it has ended, refused the steer, or the text is empty.</summary>
    NotSteerable
}

/// <summary>The eventual result of one accepted CodeBuddy steer.</summary>
public enum CodebuddySteerOutcome
{
    /// <summary>CodeBuddy's output shows the steered message entering the run.</summary>
    Consumed,

    /// <summary>The run ended, was cancelled, or CodeBuddy exited before the steer entered the run.</summary>
    NotConsumed
}

/// <summary>Outcome data for one accepted CodeBuddy steer.</summary>
public sealed class CodebuddySteerOutcomeEventArgs(string steerId, CodebuddySteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the caller-provided steer identity.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether the run consumed the steer.</summary>
    public CodebuddySteerOutcome Outcome { get; } = outcome;
}

/// <summary>Per-execution channel for steering a CodeBuddy run.</summary>
/// <remarks>
/// Create a new channel for each execution. The provider attaches it only while the run is iterated and the steering transport
/// is connected; outside that window <see cref="SteerAsync"/> returns <see cref="CodebuddySteerResult.NotSteerable"/> and writes
/// nothing. A steer is accepted once CodeBuddy acknowledges it, and its outcome is then reported through
/// <see cref="OutcomeObserved"/> exactly once.
/// </remarks>
public sealed class CodebuddyRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<CodebuddySteerResult>>? _sink;

    /// <summary>Raised once for every accepted steer when its outcome is resolved.</summary>
    public event EventHandler<CodebuddySteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>Asks the attached execution to deliver follow-up text to the running turn.</summary>
    /// <returns>
    /// <see cref="CodebuddySteerResult.NotSteerable"/> for an empty or whitespace-only text, which is never written.
    /// </returns>
    public Task<CodebuddySteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);

        Func<string, string, CancellationToken, Task<CodebuddySteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        if (sink is null || string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(CodebuddySteerResult.NotSteerable);
        }

        return sink(steerId, text, cancellationToken);
    }

    internal void Attach(Func<string, string, CancellationToken, Task<CodebuddySteerResult>> sink)
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

    internal void Report(string steerId, CodebuddySteerOutcome outcome)
    {
        OutcomeObserved?.Invoke(this, new CodebuddySteerOutcomeEventArgs(steerId, outcome));
    }
}
