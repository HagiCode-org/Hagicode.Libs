using System.Text.Json;
using HagiCode.Libs.Core.Acp;

namespace HagiCode.Libs.Providers.Codebuddy;

/// <summary>
/// Tracks the steers of one CodeBuddy run over ACP. A steer is registered before its request is written, so it is either tracked
/// until it resolves or refused. An injection is matched by the message id the steer carried (<c>clientUserMessageId</c>), and the
/// window closes when the run's terminal message arrives. Consumption is never timed: only the wait for the acknowledgement is.
/// </summary>
internal sealed class CodebuddySteerTracker
{
    private readonly object _gate = new();
    private readonly Func<string, string, CancellationToken, Task<JsonElement>> _sendSteer;
    private readonly TimeSpan _deliveryTimeout;
    private readonly List<SteerState> _steers = [];
    private readonly Dictionary<string, SteerState> _openById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownIds = new(StringComparer.Ordinal);
    private bool _windowOpen = true;

    /// <summary>
    /// Creates a tracker for one run.
    /// </summary>
    /// <param name="sendSteer">Writes the <c>session/steer</c> request and returns its reply.</param>
    /// <param name="deliveryTimeout">How long a steer waits for the reply. It does not bound consumption.</param>
    public CodebuddySteerTracker(Func<string, string, CancellationToken, Task<JsonElement>> sendSteer, TimeSpan deliveryTimeout)
    {
        _sendSteer = sendSteer ?? throw new ArgumentNullException(nameof(sendSteer));
        _deliveryTimeout = deliveryTimeout;
    }

    /// <summary>
    /// Sends one steer and reports whether CodeBuddy acknowledged it.
    /// </summary>
    public async Task<CodebuddySteerResult> SteerAsync(string steerId, string text, CancellationToken cancellationToken = default)
    {
        SteerState state;
        lock (_gate)
        {
            if (!_windowOpen || !_knownIds.Add(steerId))
            {
                return CodebuddySteerResult.NotSteerable;
            }

            state = new SteerState(steerId);
            _steers.Add(state);
            _openById[steerId] = state;
        }

        JsonElement reply;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_deliveryTimeout);
            reply = await _sendSteer(steerId, text, deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A refusal, a failed write, a timeout or a cancelled caller: the steer was never acknowledged, so it gets no outcome.
            Abandon(state);
            return CodebuddySteerResult.NotSteerable;
        }

        lock (_gate)
        {
            if (state.Closed)
            {
                return CodebuddySteerResult.NotSteerable;
            }

            if (!IsSteered(reply))
            {
                Abandon(state);
                return CodebuddySteerResult.NotSteerable;
            }

            state.Accepted = true;
        }

        return CodebuddySteerResult.Sent;
    }

    /// <summary>
    /// Reads one notification. Returns <see langword="true" /> when it is the echo of a steer this run registered, which must not
    /// become conversation output.
    /// </summary>
    public bool Observe(AcpNotification notification)
    {
        if (!TryGetUserMessageId(notification, out var messageId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_knownIds.Contains(messageId))
            {
                return false;
            }

            if (_openById.TryGetValue(messageId, out var state))
            {
                state.Injected = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Stops accepting steers. Called when the run's terminal message arrives, or when the run ends for any other reason.
    /// </summary>
    public void CloseWindow()
    {
        lock (_gate)
        {
            _windowOpen = false;
        }
    }

    /// <summary>
    /// Closes the window and resolves every accepted steer, in submission order. Call it only after the CLI process is disposed.
    /// </summary>
    /// <returns>One outcome per accepted steer: consumed when its injection was seen, otherwise not consumed.</returns>
    public IReadOnlyList<(string SteerId, CodebuddySteerOutcome Outcome)> Finish()
    {
        lock (_gate)
        {
            _windowOpen = false;
            var outcomes = new List<(string SteerId, CodebuddySteerOutcome Outcome)>();
            foreach (var state in _steers)
            {
                if (state.Accepted)
                {
                    outcomes.Add((state.Id, state.Injected ? CodebuddySteerOutcome.Consumed : CodebuddySteerOutcome.NotConsumed));
                }

                state.Closed = true;
            }

            _steers.Clear();
            _openById.Clear();
            return outcomes;
        }
    }

    private void Abandon(SteerState state)
    {
        lock (_gate)
        {
            state.Closed = true;
            _steers.Remove(state);
            _openById.Remove(state.Id);
        }
    }

    private static bool IsSteered(JsonElement reply)
    {
        return reply.ValueKind == JsonValueKind.Object &&
               reply.TryGetProperty("steered", out var steered) &&
               steered.ValueKind == JsonValueKind.True;
    }

    private static bool TryGetUserMessageId(AcpNotification notification, out string messageId)
    {
        messageId = string.Empty;
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object ||
            !notification.Parameters.TryGetProperty("update", out var update) ||
            update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty("sessionUpdate", out var kind) ||
            kind.ValueKind != JsonValueKind.String ||
            !string.Equals(kind.GetString(), "user_message_chunk", StringComparison.Ordinal) ||
            !update.TryGetProperty("_meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("codebuddy.ai/messageId", out var id) ||
            id.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        messageId = id.GetString() ?? string.Empty;
        return messageId.Length > 0;
    }

    private sealed class SteerState(string id)
    {
        public string Id { get; } = id;

        public bool Accepted { get; set; }

        public bool Injected { get; set; }

        public bool Closed { get; set; }
    }
}
