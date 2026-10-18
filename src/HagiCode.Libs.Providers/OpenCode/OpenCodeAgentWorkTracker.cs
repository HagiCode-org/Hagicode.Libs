using System.Text.Json;
using HagiCode.Libs.Core.AgentWork;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// The agent work settings of one turn: where snapshots are delivered and how long the turn may be held open for running children.
/// </summary>
/// <param name="Emit">Receives each changed snapshot, in stream order, on the turn's reading thread. It must not block.</param>
/// <param name="WaitTimeout">How long a held turn waits for its children. Positive, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
/// <param name="SettleInterval">How long a held turn waits, once no child is active, for a follow-up turn of the prompted session. Zero or positive.</param>
public sealed record OpenCodeAgentWorkTurn(Action<AgentWorkSnapshot> Emit, TimeSpan WaitTimeout, TimeSpan SettleInterval)
{
    /// <summary>
    /// Rejects timeouts the hold-open loop cannot use. <see cref="OpenCodeHttpClient.PromptAsync(string, OpenCodeSessionPromptRequest, OpenCodeTurnInteraction?, OpenCodeAgentWorkTurn?, CancellationToken)"/> calls it.
    /// </summary>
    public void Validate()
    {
        ValidateTimeouts(WaitTimeout, SettleInterval);
    }

    internal static void ValidateTimeouts(TimeSpan waitTimeout, TimeSpan settleInterval)
    {
        if (waitTimeout != Timeout.InfiniteTimeSpan && waitTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                "AgentWorkWaitTimeout",
                waitTimeout,
                "AgentWorkWaitTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (settleInterval == Timeout.InfiniteTimeSpan || settleInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                "AgentWorkSettleInterval",
                settleInterval,
                "AgentWorkSettleInterval must be zero or positive and finite.");
        }
    }
}

/// <summary>
/// Tracks the child sessions of one prompted OpenCode session and builds the agent work snapshot of the run. A child is any session
/// whose <c>parentID</c> chain leads to the prompted session. Other sessions on a shared server are ignored. Child text never enters
/// the prompted session's transcript; the tracker only observes.
/// </summary>
internal sealed class OpenCodeAgentWorkTracker
{
    /// <summary>The provider key that prefixes every item id of an OpenCode run.</summary>
    internal const string ProviderKey = "opencode";

    /// <summary>The longest title kept, in characters, including the ellipsis.</summary>
    internal const int MaxTitleLength = 200;

    /// <summary>The longest summary kept, in characters, including the ellipsis.</summary>
    internal const int MaxSummaryLength = 4000;

    private const string SessionCreatedEvent = "session.created";
    private const string SessionTextEndedEvent = "session.text.ended";
    private const string InboxEnqueuedEvent = "session.inbox.enqueued";
    private const string ExecutionStartedEvent = "session.execution.started";
    private const string ExecutionSucceededEvent = "session.execution.succeeded";
    private const string ExecutionFailedEvent = "session.execution.failed";
    private const string ExecutionInterruptedEvent = "session.execution.interrupted";
    private const string ToolSuccessEvent = "session.tool.success";
    private const string ToolFailedEvent = "session.tool.failed";

    private readonly string _promptedSessionId;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ChildRecord> _children = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unrelatedSessions = new(StringComparer.Ordinal);
    private readonly List<ChildRecord> _order = [];
    private string? _lastSignature;
    private long _sequence;
    private bool _holding;

    /// <summary>
    /// Creates a tracker for one turn of <paramref name="promptedSessionId"/>.
    /// </summary>
    /// <param name="promptedSessionId">The session the turn prompts. Its children are the items.</param>
    /// <param name="timeProvider">The clock for start and end times, or <see langword="null"/> for the system clock.</param>
    public OpenCodeAgentWorkTracker(string promptedSessionId, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(promptedSessionId);
        _promptedSessionId = promptedSessionId;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Whether a tracked child is still starting or running.
    /// </summary>
    public bool HasActiveWork => _order.Exists(static record => AgentWorkStatuses.IsActive(record.Status));

    /// <summary>
    /// Observes one event of the event stream. Events of sessions that are not tracked change nothing.
    /// </summary>
    public void Observe(OpenCodeEventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var sessionId = envelope.SessionId;
        var eventType = envelope.EventType;
        if (sessionId is null || eventType is null)
        {
            return;
        }

        if (string.Equals(sessionId, _promptedSessionId, StringComparison.Ordinal))
        {
            if (string.Equals(eventType, InboxEnqueuedEvent, StringComparison.Ordinal))
            {
                ObserveInboxItem(envelope.Payload);
            }

            return;
        }

        if (string.Equals(eventType, SessionCreatedEvent, StringComparison.Ordinal))
        {
            ObserveCreated(sessionId, envelope.Payload);
            return;
        }

        if (!_children.TryGetValue(sessionId, out var child))
        {
            return;
        }

        switch (eventType)
        {
            case ExecutionStartedEvent:
                ApplyStatus(child, AgentWorkStatuses.Running, null);
                return;

            case ExecutionSucceededEvent:
                ApplyStatus(child, AgentWorkStatuses.Completed, child.LastText);
                return;

            case ExecutionFailedEvent:
                ApplyStatus(child, AgentWorkStatuses.Failed, ReadFailureMessage(envelope.Payload));
                return;

            case ExecutionInterruptedEvent:
                ApplyStatus(child, AgentWorkStatuses.Stopped, null);
                return;

            case SessionTextEndedEvent:
                var text = ReadString(envelope.Payload, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    child.LastText = text;
                }

                return;

            case ToolSuccessEvent:
            case ToolFailedEvent:
                // A finished tool call counts whether it succeeded or failed.
                child.ToolUses++;
                return;
        }
    }

    /// <summary>
    /// Returns the complete list of agent work when it differs from the list last returned, or <see langword="null"/> when nothing
    /// changed. A turn that has observed no child returns <see langword="null"/>, so an empty list is never the first snapshot.
    /// </summary>
    public AgentWorkSnapshot? TakeSnapshot()
    {
        var items = BuildItems();
        if (items.Count == 0 && _lastSignature is null)
        {
            return null;
        }

        var signature = JsonSerializer.Serialize(items, AgentWorkSnapshot.JsonOptions);
        if (string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            return null;
        }

        _lastSignature = signature;
        _sequence++;
        return new AgentWorkSnapshot(_sequence, items);
    }

    /// <summary>
    /// Marks the active children as blocking completion while the turn is held open for them.
    /// </summary>
    public void HoldActive()
    {
        _holding = true;
    }

    /// <summary>
    /// Ends the hold once no child is active. The items no longer block completion.
    /// </summary>
    public void ReleaseHold()
    {
        _holding = false;
    }

    /// <summary>
    /// Stops every active child, because the turn ends before it finished. The hold ends with them.
    /// </summary>
    public void StopActive()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var record in _order.Where(static record => AgentWorkStatuses.IsActive(record.Status)))
        {
            record.Status = AgentWorkStatuses.Stopped;
            record.EndedAt = now;
        }

        _holding = false;
    }

    private void ObserveCreated(string childSessionId, JsonElement payload)
    {
        if (_children.ContainsKey(childSessionId) || _unrelatedSessions.Contains(childSessionId))
        {
            return;
        }

        var parentSessionId = ReadParentSessionId(payload);
        ChildRecord? parent = null;
        if (parentSessionId is null
            || (!string.Equals(parentSessionId, _promptedSessionId, StringComparison.Ordinal)
                && !_children.TryGetValue(parentSessionId, out parent)))
        {
            // The parent is neither the prompted session nor a tracked child, so the session is not part of this run.
            _unrelatedSessions.Add(childSessionId);
            return;
        }

        var record = new ChildRecord(childSessionId, _timeProvider.GetUtcNow())
        {
            Title = Truncate(ReadTitle(payload), MaxTitleLength) ?? string.Empty,
            // A deeper child is listed under the top-level child, because the display shows one level below the main agent.
            ParentSessionId = parent is null ? null : parent.ParentSessionId ?? parent.SessionId,
        };
        _children[childSessionId] = record;
        _order.Add(record);
    }

    /// <summary>
    /// Reads the completion notice of a child: an inbox item of type <c>synthetic</c> whose metadata names the child and its state.
    /// A notice only updates a child that was already observed; it never creates one, and a shell notice is not a child.
    /// </summary>
    private void ObserveInboxItem(JsonElement payload)
    {
        if (!TryGetObject(payload, "item", out var item)
            || ReadString(item, "type") != "synthetic"
            || !TryGetObject(item, "payload", out var body)
            || !TryGetObject(body, "metadata", out var metadata)
            || ReadString(metadata, "source") != "subagent")
        {
            return;
        }

        var childId = ReadString(metadata, "childID");
        if (childId is null || !_children.TryGetValue(childId, out var child))
        {
            return;
        }

        ApplyStatus(child, MapSyntheticState(ReadString(metadata, "state")), null);
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        value = property;
        return true;
    }

    private void ApplyStatus(ChildRecord record, string status, string? summary)
    {
        if (record.Status == status)
        {
            if (IsFinished(status) && record.Summary is null && summary is not null)
            {
                record.Summary = Truncate(summary, MaxSummaryLength);
            }

            return;
        }

        if (AgentWorkStatuses.IsActive(status))
        {
            // A finished child that starts again reopens its own item.
            if (IsFinished(record.Status))
            {
                record.EndedAt = null;
                record.Summary = null;
            }

            record.Status = status;
            return;
        }

        // A finished result is kept: a later stop does not downgrade a completed or failed child.
        if (IsFinished(record.Status))
        {
            return;
        }

        record.Status = status;
        record.EndedAt = _timeProvider.GetUtcNow();
        record.Summary = Truncate(summary, MaxSummaryLength);
    }

    private List<AgentWorkItem> BuildItems()
    {
        var items = new List<AgentWorkItem>(_order.Count);
        foreach (var record in _order)
        {
            items.Add(new AgentWorkItem(
                Id: PrefixId(record.SessionId),
                Kind: AgentWorkKinds.Subagent,
                ParentId: record.ParentSessionId is null ? null : PrefixId(record.ParentSessionId),
                Status: record.Status,
                Title: record.Title,
                Detail: null,
                StartedAt: record.StartedAt,
                EndedAt: record.EndedAt,
                DeadlineAt: null,
                Summary: record.Summary,
                Usage: BuildUsage(record),
                ToolUseId: null,
                BlocksCompletion: _holding && AgentWorkStatuses.IsActive(record.Status)));
        }

        return items;
    }

    private static AgentWorkUsage? BuildUsage(ChildRecord record)
    {
        // Only what OpenCode reported is kept: a tool count when a tool finished, and the duration once the child ended.
        var toolUses = record.ToolUses > 0 ? record.ToolUses : (int?)null;
        long? durationMs = record.EndedAt is { } endedAt
            ? (long)(endedAt - record.StartedAt).TotalMilliseconds
            : null;
        return toolUses is null && durationMs is null
            ? null
            : new AgentWorkUsage(TotalTokens: null, ToolUses: toolUses, DurationMs: durationMs);
    }

    private static bool IsFinished(string status)
    {
        return status is AgentWorkStatuses.Completed or AgentWorkStatuses.Failed or AgentWorkStatuses.Stopped;
    }

    private static string PrefixId(string sessionId) => $"{ProviderKey}:{sessionId}";

    /// <summary>
    /// Maps the state word of a completion notice to a status. Only the known words map to a finished state. Any other word, such as the
    /// <c>cancelled</c> OpenCode sends for an interrupted child, is stopped, because showing a wrong "completed" is worse than a
    /// conservative "stopped".
    /// </summary>
    private static string MapSyntheticState(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "running" or "started" => AgentWorkStatuses.Running,
            "completed" or "succeeded" => AgentWorkStatuses.Completed,
            "failed" or "errored" => AgentWorkStatuses.Failed,
            _ => AgentWorkStatuses.Stopped,
        };
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..(maxLength - 1)] + "…";
    }

    /// <summary>
    /// The parent of a created session (<c>parentID</c>). Null for a session that has none.
    /// </summary>
    private static string? ReadParentSessionId(JsonElement payload) => ReadString(payload, "parentID");

    /// <summary>
    /// The task description the parent gave the child (the child session's <c>title</c>), or the agent name when OpenCode sent no title.
    /// </summary>
    private static string? ReadTitle(JsonElement payload) => ReadString(payload, "title") ?? ReadString(payload, "agent");

    /// <summary>
    /// The message of a failed execution (<c>error.message</c>), or null when OpenCode sent none.
    /// </summary>
    private static string? ReadFailureMessage(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object
               && payload.TryGetProperty("error", out var error)
            ? ReadString(error, "message")
            : null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private sealed class ChildRecord(string sessionId, DateTimeOffset startedAt)
    {
        public string SessionId { get; } = sessionId;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public string? ParentSessionId { get; init; }

        public string Status { get; set; } = AgentWorkStatuses.Starting;

        public string Title { get; init; } = string.Empty;

        public DateTimeOffset? EndedAt { get; set; }

        public string? Summary { get; set; }

        public string? LastText { get; set; }

        public int ToolUses { get; set; }
    }
}
