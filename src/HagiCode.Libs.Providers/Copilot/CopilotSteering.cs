namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// The result of asking a running Copilot execution to take a follow-up message.
/// </summary>
public enum CopilotSteerResult
{
    /// <summary>The message was handed to the Copilot session during its running turn. Its outcome follows through <see cref="CopilotRunSteering.OutcomeObserved" />.</summary>
    Sent,

    /// <summary>Nothing was sent: the execution has no running turn, has ended, or the runtime refused the message. No outcome follows.</summary>
    NotSteerable
}

/// <summary>
/// What became of a steer the runtime accepted. Every accepted steer is reported exactly once.
/// </summary>
public enum CopilotSteerOutcome
{
    /// <summary>The session's events show that the running turn took the message in.</summary>
    Consumed,

    /// <summary>The turn ended, failed or was cancelled, or the delivery deadline passed, before the session showed the message was taken in.</summary>
    NotConsumed
}

/// <summary>
/// Carries the outcome of one accepted steer, as reported by <see cref="CopilotRunSteering.OutcomeObserved" />.
/// </summary>
/// <param name="steerId">The id the caller gave the steer.</param>
/// <param name="outcome">Whether the running turn consumed the steer.</param>
public sealed class CopilotSteerOutcomeEventArgs(string steerId, CopilotSteerOutcome outcome) : EventArgs
{
    /// <summary>Gets the id the caller gave the steer.</summary>
    public string SteerId { get; } = steerId;

    /// <summary>Gets whether the running turn consumed the steer.</summary>
    public CopilotSteerOutcome Outcome { get; } = outcome;
}

/// <summary>
/// Injects follow-up text into one running Copilot execution, inside its active turn.
/// </summary>
/// <remarks>
/// Create one handle per execution and pass it in <see cref="CopilotOptions.Steering" />. The provider attaches the handle while
/// the execution is iterated and detaches it when iteration ends. While it is detached, <see cref="SteerAsync" /> returns
/// <see cref="CopilotSteerResult.NotSteerable" /> and sends nothing.
/// <para>
/// A steer is sent with the immediate delivery mode and carries text only: no attachment, and no abort or second turn. The
/// session takes it in at a model-call boundary, typically after a tool result. For a text-only reply it is processed after
/// that reply, still inside the same run. The session's <c>user.message</c> event with the same text confirms consumption, and
/// steers with identical text are matched to those events in submission order. A steer that is still unconfirmed when the turn
/// ends, the execution is cancelled or fails, or <see cref="CopilotOptions.SteerDeliveryTimeout" /> passes is reported as
/// <see cref="CopilotSteerOutcome.NotConsumed" />.
/// </para>
/// <para>
/// The event is raised on the thread that reads the session events. Handlers should only record the outcome and return.
/// </para>
/// </remarks>
public sealed class CopilotRunSteering
{
    private readonly object _gate = new();
    private Func<string, string, CancellationToken, Task<CopilotSteerResult>>? _sink;

    /// <summary>
    /// Raised once for every accepted steer, with its outcome.
    /// </summary>
    public event EventHandler<CopilotSteerOutcomeEventArgs>? OutcomeObserved;

    /// <summary>
    /// Asks the running execution to take a follow-up message into its active turn.
    /// </summary>
    /// <param name="steerId">A caller-chosen id that is unique among the steers of this execution.</param>
    /// <param name="text">The text of the follow-up message. It is sent as text only.</param>
    /// <param name="cancellationToken">Cancels the send. A steer that the session already accepted is not withdrawn.</param>
    /// <returns>
    /// <see cref="CopilotSteerResult.Sent" /> when the session accepted the message during the running turn;
    /// <see cref="CopilotSteerResult.NotSteerable" /> when the execution is not attached, has no running turn, has ended, already
    /// has a steer with that id, or the runtime refused the message.
    /// </returns>
    public Task<CopilotSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        Func<string, string, CancellationToken, Task<CopilotSteerResult>>? sink;
        lock (_gate)
        {
            sink = _sink;
        }

        return sink is null
            ? Task.FromResult(CopilotSteerResult.NotSteerable)
            : sink(steerId, text, cancellationToken);
    }

    /// <summary>
    /// Connects the handle to a running execution. Called by the provider while the execution is iterated.
    /// </summary>
    internal void Attach(Func<string, string, CancellationToken, Task<CopilotSteerResult>> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sink = sink;
        }
    }

    /// <summary>
    /// Disconnects the handle. Called by the provider when iteration ends.
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
    internal void Report(string steerId, CopilotSteerOutcome outcome)
    {
        OutcomeObserved?.Invoke(this, new CopilotSteerOutcomeEventArgs(steerId, outcome));
    }
}

/// <summary>
/// Tracks the steers of one Copilot run. The provider creates it for each execution that has a <see cref="CopilotRunSteering" />
/// handle, and the gateway drives it: it starts with the SendAndWait of the run, sees the session's user messages, and closes
/// before the run's terminal event is written.
/// </summary>
/// <remarks>
/// Every steer send is initiated under the same gate that Close takes, so a steer is either refused, or initiated before the
/// close and then tracked. A send that the session accepted after the close is reported as not consumed right away. A send
/// that failed is never reported, because the caller owns it from then on.
/// </remarks>
internal sealed class CopilotSteerTurn
{
    private readonly object _gate = new();
    private readonly CopilotRunSteering _handle;
    private readonly TimeSpan _deliveryTimeout;

    // Submission order. The session echoes identical texts in this order, so each echo is matched to the first entry with its text.
    private readonly List<PendingSteer> _steers = [];
    private Func<string, CancellationToken, Task>? _sendSteer;
    private bool _running;
    private bool _closed;

    public CopilotSteerTurn(CopilotRunSteering handle, TimeSpan deliveryTimeout)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        _deliveryTimeout = deliveryTimeout;
    }

    /// <summary>
    /// Starts the run: marks steers acceptable and starts the turn in the same gate, so no steer reaches the session before the
    /// turn is started and none after it is closed.
    /// </summary>
    /// <param name="sendSteer">Sends one steer text to the session, immediate and text only.</param>
    /// <param name="startTurn">Starts the SendAndWait of the run and returns its task.</param>
    public Task Start(Func<string, CancellationToken, Task> sendSteer, Func<Task> startTurn)
    {
        ArgumentNullException.ThrowIfNull(sendSteer);
        ArgumentNullException.ThrowIfNull(startTurn);

        lock (_gate)
        {
            _sendSteer = sendSteer;
            _running = true;
            try
            {
                return startTurn();
            }
            catch
            {
                _running = false;
                throw;
            }
        }
    }

    /// <summary>
    /// Accepts or refuses one steer. Attached to <see cref="CopilotRunSteering" /> by the provider.
    /// </summary>
    public Task<CopilotSteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken)
    {
        PendingSteer steer;
        Task sendTask;
        lock (_gate)
        {
            if (!_running || _sendSteer is null || _steers.Exists(pending => pending.SteerId == steerId))
            {
                return Task.FromResult(CopilotSteerResult.NotSteerable);
            }

            steer = new PendingSteer(steerId, text);
            _steers.Add(steer);
            try
            {
                // Initiated under the gate, so Close (which takes the same gate) orders against this send.
                sendTask = _sendSteer(text, cancellationToken);
            }
            catch (Exception)
            {
                Remove(steer);
                return Task.FromResult(CopilotSteerResult.NotSteerable);
            }
        }

        return AwaitAcceptanceAsync(steer, sendTask);
    }

    /// <summary>
    /// Matches a user message of the session to a steer with the same text, the first one in submission order. Called for every
    /// <c>user.message</c> event of the session.
    /// </summary>
    public void ObserveUserMessage(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        PendingSteer? consumed = null;
        lock (_gate)
        {
            var steer = _steers.Find(pending => string.Equals(pending.Text, content, StringComparison.Ordinal));
            if (steer is null)
            {
                return;
            }

            Remove(steer);

            // A steer that already timed out was reported as not consumed. Its echo only consumes its slot in the order.
            if (steer.State == SteerState.TimedOut)
            {
                return;
            }

            consumed = steer;
        }

        _handle.Report(consumed.SteerId, CopilotSteerOutcome.Consumed);
    }

    /// <summary>
    /// Ends the run: refuses further steers and reports every accepted steer that is still unconfirmed as not consumed. Steers still
    /// being sent are resolved when their send completes. Safe to call more than once.
    /// </summary>
    public void Close()
    {
        var unconfirmed = new List<PendingSteer>();
        lock (_gate)
        {
            _running = false;
            _closed = true;
            foreach (var steer in _steers.FindAll(pending => pending.State != SteerState.Sending))
            {
                if (steer.State == SteerState.Accepted)
                {
                    unconfirmed.Add(steer);
                }

                Remove(steer);
            }
        }

        foreach (var steer in unconfirmed)
        {
            _handle.Report(steer.SteerId, CopilotSteerOutcome.NotConsumed);
        }
    }

    private async Task<CopilotSteerResult> AwaitAcceptanceAsync(PendingSteer steer, Task sendTask)
    {
        try
        {
            await sendTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The runtime refused the message, so it never reached the session and gets no outcome. An echo that already arrived
            // proves the session took it, so the result is Sent.
            lock (_gate)
            {
                if (steer.Removed)
                {
                    return CopilotSteerResult.Sent;
                }

                Remove(steer);
            }

            return CopilotSteerResult.NotSteerable;
        }

        CopilotSteerOutcome? closedOutcome = null;
        lock (_gate)
        {
            if (!steer.Removed)
            {
                if (_closed)
                {
                    // The run ended while the send was in flight. The session accepted it, so it is reported now.
                    Remove(steer);
                    closedOutcome = CopilotSteerOutcome.NotConsumed;
                }
                else
                {
                    steer.State = SteerState.Accepted;
                    ArmDeadline(steer);
                }
            }
        }

        if (closedOutcome is { } outcome)
        {
            _handle.Report(steer.SteerId, outcome);
        }

        return CopilotSteerResult.Sent;
    }

    private void ArmDeadline(PendingSteer steer)
    {
        var deadline = new CancellationTokenSource();
        steer.Deadline = deadline;
        _ = ExpireAsync(steer, deadline.Token);
    }

    private async Task ExpireAsync(PendingSteer steer, CancellationToken token)
    {
        try
        {
            await Task.Delay(_deliveryTimeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (steer.Removed || steer.State != SteerState.Accepted)
            {
                return;
            }

            // The entry keeps its place in the order: the session may still echo it, and that echo must not be taken by a later steer.
            steer.State = SteerState.TimedOut;
        }

        _handle.Report(steer.SteerId, CopilotSteerOutcome.NotConsumed);
    }

    // Must be called under _gate.
    private void Remove(PendingSteer steer)
    {
        steer.Removed = true;
        steer.Deadline?.Cancel();
        _steers.Remove(steer);
    }

    private enum SteerState
    {
        Sending,
        Accepted,
        TimedOut
    }

    private sealed class PendingSteer(string steerId, string text)
    {
        public string SteerId { get; } = steerId;

        public string Text { get; } = text;

        public SteerState State { get; set; } = SteerState.Sending;

        public CancellationTokenSource? Deadline { get; set; }

        public bool Removed { get; set; }
    }
}
