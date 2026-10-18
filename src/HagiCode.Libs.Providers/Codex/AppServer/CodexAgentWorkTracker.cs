using System.Text.Json;
using HagiCode.Libs.Core.AgentWork;

namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// Turns Codex collaboration signals into the provider-neutral agent work snapshot. It only observes: it never changes when a
/// turn ends, and it never adds transcript text, tool calls or statistics.
/// </summary>
/// <remarks>
/// The app-server transport reports a subagent from its <c>subAgentActivity</c> items and follows the subagent's own thread for
/// status and usage. The exec transport reports only the agent states carried by <c>collab_tool_call</c> items. Both feed the same
/// records. Input is read as raw JSON, because the vendored exec models cannot represent every value the protocol sends (for
/// example <c>interrupt_agent</c> or <c>interrupted</c>). Names are matched without underscores and case, so the camelCase
/// app-server spelling and the snake_case exec spelling share one rule.
/// </remarks>
internal sealed class CodexAgentWorkTracker
{
    /// <summary>The provider key that prefixes every item id of a Codex run.</summary>
    internal const string ProviderKey = "codex";

    /// <summary>The longest title kept, in characters, including the ellipsis.</summary>
    internal const int MaxTitleLength = 200;

    /// <summary>The longest summary kept, in characters, including the ellipsis.</summary>
    internal const int MaxSummaryLength = 4000;

    private const string ItemStartedMethod = "item/started";
    private const string ItemCompletedMethod = "item/completed";
    private const string TurnStartedMethod = "turn/started";
    private const string TurnCompletedMethod = "turn/completed";
    private const string ThreadStatusChangedMethod = "thread/status/changed";
    private const string TokenUsageUpdatedMethod = "thread/tokenUsage/updated";
    private const string CollabItemType = "collabAgentToolCall";
    private const string CollabExecItemType = "collab_tool_call";
    private const string ActivityItemType = "subAgentActivity";
    private const string AgentMessageItemType = "agentMessage";
    private const string SpawnTool = "spawnagent";
    private const string SystemErrorStatus = "systemerror";

    private static readonly HashSet<string> ToolLikeItemTypes = new(StringComparer.Ordinal)
    {
        "commandExecution",
        "fileChange",
        "mcpToolCall",
        "webSearch",
        "dynamicToolCall"
    };

    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, WorkRecord> _records = new(StringComparer.Ordinal);
    private readonly List<WorkRecord> _order = [];
    private string? _lastSignature;
    private long _sequence;

    /// <summary>
    /// Creates a tracker for one run.
    /// </summary>
    /// <param name="timeProvider">The clock for start and end times, or <see langword="null"/> for the system clock.</param>
    public CodexAgentWorkTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Observes one app-server notification of any thread. A notification that does not concern a subagent changes nothing.
    /// </summary>
    public void Observe(string method, JsonElement parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        var threadId = ReadString(parameters, "threadId");
        switch (method)
        {
            case ItemStartedMethod:
                ObserveItem(parameters, threadId, completed: false);
                return;

            case ItemCompletedMethod:
                ObserveItem(parameters, threadId, completed: true);
                return;

            case TurnStartedMethod:
                // A turn that starts on a known subagent thread means the subagent works (again).
                if (threadId is not null && _records.TryGetValue(threadId, out var startedRecord))
                {
                    ApplyStatus(startedRecord, AgentWorkStatuses.Running, null);
                }

                return;

            case TurnCompletedMethod:
                ObserveTurnCompleted(parameters, threadId);
                return;

            case ThreadStatusChangedMethod:
                if (threadId is not null
                    && _records.TryGetValue(threadId, out var statusRecord)
                    && TryGetObject(parameters, "status", out var status)
                    && ToKey(ReadString(status, "type")) == SystemErrorStatus)
                {
                    ApplyStatus(statusRecord, AgentWorkStatuses.Failed, null);
                }

                return;

            case TokenUsageUpdatedMethod:
                ObserveTokenUsage(parameters, threadId);
                return;
        }
    }

    /// <summary>
    /// Observes one line of the <c>codex exec --json</c> stream and returns a snapshot when the agent work changed. Lines that are
    /// not collaboration items return <see langword="null"/> without parsing.
    /// </summary>
    /// <exception cref="JsonException">The line is not valid JSON.</exception>
    public AgentWorkSnapshot? ObserveExecLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.Contains(CollabExecItemType, StringComparison.Ordinal))
        {
            return null;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetObject(root, "item", out var item)
            || ReadString(item, "type") != CollabExecItemType)
        {
            return null;
        }

        ObserveCollab(item);
        return TakeSnapshot();
    }

    /// <summary>
    /// Returns the complete list of agent work when it differs from the list last returned, or <see langword="null"/> when nothing
    /// changed. A run that has observed no subagent returns <see langword="null"/>, so an empty list is never the first snapshot.
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
    /// The complete list as it stands now, whether or not it changed since the last snapshot was taken.
    /// </summary>
    internal IReadOnlyList<AgentWorkItem> CurrentItems => BuildItems();

    private void ObserveItem(JsonElement parameters, string? threadId, bool completed)
    {
        if (!TryGetObject(parameters, "item", out var item))
        {
            return;
        }

        var type = ReadString(item, "type");
        switch (type)
        {
            case CollabItemType:
                ObserveCollab(item);
                return;

            case ActivityItemType:
                ObserveActivity(item, threadId);
                return;

            default:
                if (completed
                    && type is not null
                    && ToolLikeItemTypes.Contains(type)
                    && threadId is not null
                    && _records.TryGetValue(threadId, out var record))
                {
                    record.ToolUses = (record.ToolUses ?? 0) + 1;
                }

                return;
        }
    }

    private void ObserveCollab(JsonElement item)
    {
        if (ToKey(ReadString(item, "tool")) == SpawnTool)
        {
            var parent = ResolveParent(ReadString(item, "senderThreadId", "sender_thread_id"));
            var prompt = ReadString(item, "prompt");
            var callId = ReadString(item, "id");
            foreach (var receiver in ReadStrings(item, "receiverThreadIds", "receiver_thread_ids"))
            {
                if (!_records.ContainsKey(receiver))
                {
                    AddRecord(receiver, prompt, parent, callId);
                }
            }
        }

        // Only known threads are updated: a state for a thread the run never spawned or announced creates nothing.
        if (!TryGetObject(item, "agentsStates", "agents_states", out var states))
        {
            return;
        }

        foreach (var state in states.EnumerateObject())
        {
            if (!_records.TryGetValue(state.Name, out var record))
            {
                continue;
            }

            var providerStatus = ReadString(state.Value, "status");
            if (providerStatus is null)
            {
                continue;
            }

            ApplyStatus(record, MapCollabState(providerStatus), ReadString(state.Value, "message"));
        }
    }

    private void ObserveActivity(JsonElement item, string? reportingThreadId)
    {
        var threadId = ReadString(item, "agentThreadId", "agent_thread_id");
        var kind = ToKey(ReadString(item, "kind"));
        if (threadId is null || kind is null || threadId == reportingThreadId)
        {
            return;
        }

        // The thread that reports the activity is the one that spawned the subagent.
        if (!_records.TryGetValue(threadId, out var record))
        {
            record = AddRecord(threadId, ReadString(item, "agentPath"), ResolveParent(reportingThreadId), toolUseId: null);
        }

        switch (kind)
        {
            case "started":
                ApplyStatus(record, AgentWorkStatuses.Running, null);
                return;

            case "interacted":
                // Input to a finished subagent does not change its status; it only starts a subagent that has not begun.
                if (record.Status == AgentWorkStatuses.Starting)
                {
                    ApplyStatus(record, AgentWorkStatuses.Running, null);
                }

                return;

            case "interrupted":
                ApplyStatus(record, AgentWorkStatuses.Stopped, null);
                return;

            case "completed":
                ApplyStatus(record, AgentWorkStatuses.Completed, null);
                return;
        }
    }

    private void ObserveTurnCompleted(JsonElement parameters, string? threadId)
    {
        if (threadId is null
            || !_records.TryGetValue(threadId, out var record)
            || !TryGetObject(parameters, "turn", out var turn))
        {
            return;
        }

        var status = ToKey(ReadString(turn, "status")) switch
        {
            "completed" => AgentWorkStatuses.Completed,
            "failed" => AgentWorkStatuses.Failed,
            "interrupted" => AgentWorkStatuses.Stopped,
            _ => null
        };
        if (status is null)
        {
            return;
        }

        ApplyStatus(record, status, status == AgentWorkStatuses.Completed ? LastAgentMessage(turn) : null);
    }

    private void ObserveTokenUsage(JsonElement parameters, string? threadId)
    {
        if (threadId is null
            || !_records.TryGetValue(threadId, out var record)
            || !TryGetObject(parameters, "tokenUsage", out var tokenUsage)
            || !TryGetObject(tokenUsage, "last", out var last)
            || !TryReadNumber(last, "totalTokens", out var totalTokens))
        {
            return;
        }

        record.TotalTokens = (record.TotalTokens ?? 0) + totalTokens;
    }

    private WorkRecord? ResolveParent(string? threadId)
    {
        return threadId is not null && _records.TryGetValue(threadId, out var parent) ? parent : null;
    }

    private WorkRecord AddRecord(string threadId, string? title, WorkRecord? parent, string? toolUseId)
    {
        var record = new WorkRecord(threadId, _timeProvider.GetUtcNow())
        {
            Title = Truncate(title, MaxTitleLength) ?? string.Empty,
            // A deeper subagent is listed under the top-level ancestor, because the display shows only one level below the main agent.
            ParentThreadId = parent is null ? null : parent.ParentThreadId ?? parent.ThreadId,
            ToolUseId = toolUseId
        };
        _records[threadId] = record;
        _order.Add(record);
        return record;
    }

    private void ApplyStatus(WorkRecord record, string status, string? summary)
    {
        if (record.Status == status)
        {
            if (IsFinished(status) && record.Summary is null && summary is not null)
            {
                record.Summary = Truncate(summary, MaxSummaryLength);
            }

            return;
        }

        if (IsActive(status))
        {
            // A finished subagent that works again reopens its own item instead of creating a second one.
            if (IsFinished(record.Status))
            {
                record.EndedAt = null;
                record.Summary = null;
            }

            record.Status = status;
            return;
        }

        // A finished result is kept: a later close that reports a stop does not downgrade a completed or failed subagent.
        if (record.Status is AgentWorkStatuses.Completed or AgentWorkStatuses.Failed)
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
                Id: PrefixId(record.ThreadId),
                Kind: AgentWorkKinds.Subagent,
                ParentId: record.ParentThreadId is null ? null : PrefixId(record.ParentThreadId),
                Status: record.Status,
                Title: record.Title,
                Detail: null,
                StartedAt: record.StartedAt,
                EndedAt: record.EndedAt,
                DeadlineAt: null,
                Summary: record.Summary,
                Usage: BuildUsage(record),
                ToolUseId: record.ToolUseId,
                BlocksCompletion: false));
        }

        return items;
    }

    private AgentWorkUsage? BuildUsage(WorkRecord record)
    {
        if (record.TotalTokens is null && record.ToolUses is null)
        {
            return null;
        }

        // The duration is reported only with some usage, so an item the provider reported nothing about has no usage at all.
        long? durationMs = record.EndedAt is { } endedAt
            ? (long)(endedAt - record.StartedAt).TotalMilliseconds
            : null;
        return new AgentWorkUsage(record.TotalTokens, record.ToolUses, durationMs);
    }

    private static string PrefixId(string threadId) => $"{ProviderKey}:{threadId}";

    private static string MapCollabState(string providerStatus)
    {
        // Showing a wrong "completed" is worse than a conservative "stopped", so only known words map to the other two.
        return ToKey(providerStatus) switch
        {
            "pendinginit" => AgentWorkStatuses.Starting,
            "running" => AgentWorkStatuses.Running,
            "completed" => AgentWorkStatuses.Completed,
            "errored" or "failed" => AgentWorkStatuses.Failed,
            _ => AgentWorkStatuses.Stopped
        };
    }

    private static bool IsActive(string status) => status is AgentWorkStatuses.Starting or AgentWorkStatuses.Running;

    private static bool IsFinished(string status) => status is AgentWorkStatuses.Completed or AgentWorkStatuses.Failed or AgentWorkStatuses.Stopped;

    private static string? LastAgentMessage(JsonElement turn)
    {
        string? text = null;
        if (TryGetArray(turn, "items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ReadString(item, "type") == AgentMessageItemType)
                {
                    text = ReadString(item, "text") ?? text;
                }
            }
        }

        return text;
    }

    /// <summary>
    /// The name with its underscores and case removed, so <c>spawn_agent</c>, <c>spawnAgent</c> and <c>SpawnAgent</c> compare equal.
    /// </summary>
    private static string? ToKey(string? value)
    {
        return value is null ? null : new string(value.Where(char.IsLetter).ToArray()).ToLowerInvariant();
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength - 1), "…");
    }

    private static string? ReadString(JsonElement element, string name, string? alternateName = null)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var value = FindProperty(element, name, alternateName);
        return value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;
    }

    private static IEnumerable<string> ReadStrings(JsonElement element, string name, string alternateName)
    {
        if (!TryGetArray(element, name, alternateName, out var array))
        {
            yield break;
        }

        foreach (var value in array.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
            {
                yield return text;
            }
        }
    }

    private static bool TryReadNumber(JsonElement element, string name, out long value)
    {
        var property = FindProperty(element, name, alternateName: null);
        if (property is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        return TryGetObject(element, name, alternateName: null, out value);
    }

    private static bool TryGetObject(JsonElement element, string name, string? alternateName, out JsonElement value)
    {
        var property = FindProperty(element, name, alternateName);
        if (property is { ValueKind: JsonValueKind.Object } found)
        {
            value = found;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        return TryGetArray(element, name, alternateName: null, out value);
    }

    private static bool TryGetArray(JsonElement element, string name, string? alternateName, out JsonElement value)
    {
        var property = FindProperty(element, name, alternateName);
        if (property is { ValueKind: JsonValueKind.Array } found)
        {
            value = found;
            return true;
        }

        value = default;
        return false;
    }

    private static JsonElement? FindProperty(JsonElement element, string name, string? alternateName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(name, out var value))
        {
            return value;
        }

        return alternateName is not null && element.TryGetProperty(alternateName, out var alternate) ? alternate : null;
    }

    private sealed class WorkRecord(string threadId, DateTimeOffset startedAt)
    {
        public string ThreadId { get; } = threadId;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public string Title { get; set; } = string.Empty;

        public string? ParentThreadId { get; init; }

        public string? ToolUseId { get; init; }

        public string Status { get; set; } = AgentWorkStatuses.Starting;

        public DateTimeOffset? EndedAt { get; set; }

        public string? Summary { get; set; }

        public long? TotalTokens { get; set; }

        public int? ToolUses { get; set; }
    }
}
