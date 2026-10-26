namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>The result of asking a running OpenCode execution to take a follow-up message.</summary>
public enum OpenCodeSteerResult
{
    /// <summary>OpenCode accepted the steer request. Its outcome follows through <see cref="OpenCodeRunSteering.OutcomeObserved"/>.</summary>
    Sent,

    /// <summary>The execution has no running turn, a question is pending, or OpenCode refused the request. Nothing was sent for the steer.</summary>
    NotSteerable
}

/// <summary>The eventual result of one accepted OpenCode steer.</summary>
public enum OpenCodeSteerOutcome
{
    /// <summary>OpenCode reported the steer's inbox item as delivered into the running turn.</summary>
    Consumed,

    /// <summary>The turn ended, or the deadline passed, before OpenCode reported delivery.</summary>
    NotConsumed
}

/// <summary>Outcome data for one accepted OpenCode steer.</summary>
public sealed class OpenCodeSteerOutcomeEventArgs(string steerId, OpenCodeSteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the caller-provided steer identity, which is also the message id sent to OpenCode.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether OpenCode confirmed delivery into the turn.</summary>
    public OpenCodeSteerOutcome Outcome { get; } = outcome;
}

/// <summary>Per-execution channel for steering a running OpenCode turn.</summary>
/// <remarks>
/// Create a new handle for each execution. The provider attaches it when the prompt is acknowledged and detaches it when the
/// execution's stream ends, so a steer sent earlier or later returns <see cref="OpenCodeSteerResult.NotSteerable"/> without
/// sending anything. A steer is sent as a prompt with <c>delivery: "steer"</c> and the steer's own message id. OpenCode 2.0.32
/// reports consumption with <c>session.inbox.delivered</c> carrying that id; a request that succeeds on its own is only
/// <see cref="OpenCodeSteerResult.Sent"/>. Each accepted steer resolves once: consumed on that event, or not consumed when the
/// turn ends first, the deadline passes (<see cref="OpenCodeOptions.SteerDeliveryTimeout"/>), or the stream fails. A steer is
/// never sent while a question is pending, because OpenCode holds it until the question is answered.
/// </remarks>
public sealed class OpenCodeRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<OpenCodeSteerResult>>? _sink;

    /// <summary>Raised once for every accepted steer when its outcome is resolved.</summary>
    public event EventHandler<OpenCodeSteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>Asks the attached execution to deliver follow-up text into its running turn.</summary>
    public Task<OpenCodeSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Func<string, string, CancellationToken, Task<OpenCodeSteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        return sink is null
            ? Task.FromResult(OpenCodeSteerResult.NotSteerable)
            : sink(steerId, text, cancellationToken);
    }

    internal void Attach(Func<string, string, CancellationToken, Task<OpenCodeSteerResult>> sink)
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

    internal void Report(string steerId, OpenCodeSteerOutcome outcome)
    {
        OutcomeObserved?.Invoke(this, new OpenCodeSteerOutcomeEventArgs(steerId, outcome));
    }
}

/// <summary>
/// Tracks the steers of one OpenCode turn: registers a steer before its request is sent, resolves it from the inbox events,
/// and reports every accepted steer exactly once. Only the turn loop reads the events, so the turn loop calls
/// <see cref="Delivered"/> and <see cref="EndAsync"/>.
/// </summary>
internal sealed class OpenCodeSteerTurn : IDisposable
{
    private readonly OpenCodeHttpClient _client;
    private readonly string _sessionId;
    private readonly OpenCodeRunSteering _handle;
    private readonly TimeSpan _deliveryTimeout;
    private readonly object _sync = new();

    // Serializes the request with the end of the turn, so no steer request can reach OpenCode after the turn was closed.
    // OpenCode would start a new execution for a steer sent after its turn ended.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.Ordinal);
    private Func<bool> _hasPendingForm = static () => false;
    private bool _running;

    public OpenCodeSteerTurn(OpenCodeHttpClient client, string sessionId, OpenCodeRunSteering handle, TimeSpan deliveryTimeout)
    {
        _client = client;
        _sessionId = sessionId;
        _handle = handle;
        _deliveryTimeout = deliveryTimeout;
    }

    /// <summary>Marks the turn as running and attaches the handle. Called once the prompt is acknowledged.</summary>
    public void Start(Func<bool> hasPendingForm)
    {
        lock (_sync)
        {
            _hasPendingForm = hasPendingForm;
            _running = true;
        }

        _handle.Attach(SteerAsync);
    }

    /// <summary>Resolves a steer as consumed when OpenCode reports its inbox item delivered.</summary>
    public void Delivered(string? inboxId)
    {
        if (string.IsNullOrEmpty(inboxId))
        {
            return;
        }

        lock (_sync)
        {
            if (!_pending.Remove(inboxId, out var deadline))
            {
                return;
            }

            deadline.Cancel();
        }

        _handle.Report(inboxId, OpenCodeSteerOutcome.Consumed);
    }

    /// <summary>
    /// Ends the turn: refuses further steers and resolves the leftovers as not consumed. Waits for a steer request already in
    /// flight, so the end is ordered after it. Safe to call more than once.
    /// </summary>
    public async Task EndAsync()
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        List<string> leftovers;
        try
        {
            lock (_sync)
            {
                _running = false;
                leftovers = [.. _pending.Keys];
                foreach (var deadline in _pending.Values)
                {
                    deadline.Cancel();
                }

                _pending.Clear();
            }
        }
        finally
        {
            _sendGate.Release();
        }

        foreach (var steerId in leftovers)
        {
            _handle.Report(steerId, OpenCodeSteerOutcome.NotConsumed);
        }
    }

    /// <summary>Detaches the handle. Leftover steers are already resolved by <see cref="EndAsync"/>.</summary>
    public void Dispose()
    {
        _handle.Detach();
    }

    private async Task<OpenCodeSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken)
    {
        try
        {
            await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OpenCodeSteerResult.NotSteerable;
        }

        try
        {
            CancellationTokenSource deadline;
            lock (_sync)
            {
                // Refused when the turn is not running or a question is pending. Registration comes before the request, so
                // an event for this steer cannot arrive before it is tracked.
                if (!_running || _hasPendingForm())
                {
                    return OpenCodeSteerResult.NotSteerable;
                }

                deadline = new CancellationTokenSource();
                _pending[steerId] = deadline;
            }

            try
            {
                await _client.SendSteerAsync(_sessionId, steerId, text, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // OpenCode refused the request, or it failed. The steer was never accepted, so it gets no outcome.
                RemoveSilently(steerId, deadline);
                return OpenCodeSteerResult.NotSteerable;
            }
            catch (OperationCanceledException)
            {
                RemoveSilently(steerId, deadline);
                return OpenCodeSteerResult.NotSteerable;
            }

            _ = DeadlineAsync(steerId, deadline.Token);
            return OpenCodeSteerResult.Sent;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void RemoveSilently(string steerId, CancellationTokenSource deadline)
    {
        lock (_sync)
        {
            if (_pending.Remove(steerId))
            {
                deadline.Cancel();
            }
        }
    }

    private async Task DeadlineAsync(string steerId, CancellationToken token)
    {
        try
        {
            await Task.Delay(_deliveryTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (!_pending.Remove(steerId))
            {
                return;
            }
        }

        _handle.Report(steerId, OpenCodeSteerOutcome.NotConsumed);
    }
}
