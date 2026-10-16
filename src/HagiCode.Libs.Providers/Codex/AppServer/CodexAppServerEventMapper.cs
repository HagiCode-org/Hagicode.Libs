using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ManagedCode.CodexSharpSDK.Internal;
using ManagedCode.CodexSharpSDK.Models;

namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// Maps codex app-server notifications of one turn onto the existing <see cref="ThreadEvent"/> model, so the Codex
/// provider's event handling is shared with the <c>codex exec --json</c> path.
/// </summary>
/// <remarks>
/// Usage is summed from the per-call <c>last</c> breakdown of each <c>thread/tokenUsage/updated</c> notification of the turn,
/// so a resumed process does not report the thread's earlier turns. The text of the last completed agent message is kept as
/// the turn result, which is what <c>TurnCompletedEvent.Result</c> carries on the exec path.
/// </remarks>
internal sealed class CodexAppServerEventMapper
{
    private const string TurnStartedMethod = "turn/started";
    private const string TurnCompletedMethod = "turn/completed";
    private const string ItemStartedMethod = "item/started";
    private const string ItemCompletedMethod = "item/completed";
    private const string TokenUsageUpdatedMethod = "thread/tokenUsage/updated";
    private const string ErrorMethod = "error";

    private const string TurnInterruptedMessage = "Codex turn was interrupted.";
    private const string TurnFailedMessage = "Codex turn failed.";
    private const string EmptyErrorMessage = "";

    // The app-server item types that the exec path also reports as started (tool-like items). Matched on the raw
    // app-server "type" value, because ThreadItem.Type carries the exec protocol's snake_case constant.
    private static readonly HashSet<string> StartedItemTypes = new(StringComparer.Ordinal)
    {
        "commandExecution",
        "fileChange",
        "mcpToolCall",
        "webSearch"
    };

    private long _inputTokens;
    private long _cachedInputTokens;
    private long _outputTokens;
    private string? _resultText;
    private string? _lastErrorMessage;

    /// <summary>
    /// Maps one notification of the turn <paramref name="expectedTurnId"/>. Notifications of other turns, and notifications
    /// that have no event in the model, return <see langword="null"/>. While the turn id is not known yet (a notification can
    /// arrive before the <c>turn/start</c> reply), <see langword="null"/> accepts any turn: one client runs one turn at a time.
    /// </summary>
    public ThreadEvent? Map(string method, JsonElement parameters, string? expectedTurnId)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);

        switch (method)
        {
            case TurnStartedMethod:
                return IsTurn(parameters, expectedTurnId) ? new TurnStartedEvent() : null;

            case TokenUsageUpdatedMethod:
                if (IsTurnId(parameters, expectedTurnId))
                {
                    AccumulateUsage(parameters);
                }

                return null;

            case ItemStartedMethod:
                return MapItemEvent(parameters, expectedTurnId, started: true);

            case ItemCompletedMethod:
                return MapItemEvent(parameters, expectedTurnId, started: false);

            case ErrorMethod:
                if (IsTurnId(parameters, expectedTurnId)
                    && TryGetProperty(parameters, "willRetry", out var willRetry)
                    && willRetry.ValueKind == JsonValueKind.False)
                {
                    _lastErrorMessage = ReadErrorMessage(parameters);
                }

                return null;

            case TurnCompletedMethod:
                return IsTurn(parameters, expectedTurnId) ? MapTurnCompleted(parameters) : null;

            default:
                return null;
        }
    }

    /// <summary>
    /// Parses one app-server thread item. Returns <see langword="null"/> for items that are not shown in the transcript, such
    /// as the user's own message.
    /// </summary>
    public static ThreadItem? ParseItem(JsonElement item)
    {
        var type = ReadString(item, "type") ?? string.Empty;
        var id = ReadString(item, "id") ?? string.Empty;

        return type switch
        {
            "userMessage" => null,
            "agentMessage" => new AgentMessageItem(id, ReadString(item, "text") ?? string.Empty),
            "reasoning" => new ReasoningItem(id, JoinStrings(item, "summary", "content")),
            "commandExecution" => new CommandExecutionItem(
                id,
                ReadString(item, "command") ?? string.Empty,
                ReadString(item, "aggregatedOutput") ?? string.Empty,
                ReadInt(item, "exitCode"),
                MapCommandStatus(ReadString(item, "status"))),
            "fileChange" => new FileChangeItem(id, ReadChanges(item), MapPatchStatus(ReadString(item, "status"))),
            "mcpToolCall" => new McpToolCallItem(
                id,
                ReadString(item, "server") ?? string.Empty,
                ReadString(item, "tool") ?? string.Empty,
                ReadNode(item, "arguments"),
                ReadMcpResult(item),
                ReadMcpError(item),
                MapMcpStatus(ReadString(item, "status"))),
            "webSearch" => new WebSearchItem(id, ReadString(item, "query") ?? string.Empty),
            _ => new UnknownThreadItem(id, type, item.Clone())
        };
    }

    private ThreadEvent? MapItemEvent(JsonElement parameters, string? expectedTurnId, bool started)
    {
        if (!IsTurnId(parameters, expectedTurnId)
            || !TryGetProperty(parameters, "item", out var itemElement)
            || itemElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var item = ParseItem(itemElement);
        if (item is null)
        {
            return null;
        }

        if (item is AgentMessageItem agentMessage && !started)
        {
            _resultText = agentMessage.Text;
        }

        if (started)
        {
            return StartedItemTypes.Contains(ReadString(itemElement, "type") ?? string.Empty)
                ? new ItemStartedEvent(item)
                : null;
        }

        return new ItemCompletedEvent(item);
    }

    private ThreadEvent MapTurnCompleted(JsonElement parameters)
    {
        if (!TryGetProperty(parameters, "turn", out var turn))
        {
            return new TurnFailedEvent(new ThreadError(TurnFailedMessage));
        }

        var status = ReadString(turn, "status");
        var usage = new Usage((int)_inputTokens, (int)_cachedInputTokens, (int)_outputTokens);
        switch (status)
        {
            case "completed":
                return new TurnCompletedEvent(usage, _resultText);

            case "interrupted":
                return new TurnFailedEvent(new ThreadError(TurnInterruptedMessage));

            default:
                var message = ReadErrorMessage(turn);
                if (string.IsNullOrWhiteSpace(message))
                {
                    message = _lastErrorMessage;
                }

                return new TurnFailedEvent(new ThreadError(
                    string.IsNullOrWhiteSpace(message) ? TurnFailedMessage : message));
        }
    }

    private void AccumulateUsage(JsonElement parameters)
    {
        if (!TryGetProperty(parameters, "tokenUsage", out var tokenUsage)
            || !TryGetProperty(tokenUsage, "last", out var last))
        {
            return;
        }

        _inputTokens += ReadLong(last, "inputTokens");
        _cachedInputTokens += ReadLong(last, "cachedInputTokens");
        _outputTokens += ReadLong(last, "outputTokens");
    }

    private static bool IsTurn(JsonElement parameters, string? expectedTurnId)
    {
        if (expectedTurnId is null)
        {
            return true;
        }

        return TryGetProperty(parameters, "turn", out var turn)
            && string.Equals(ReadString(turn, "id"), expectedTurnId, StringComparison.Ordinal);
    }

    private static bool IsTurnId(JsonElement parameters, string? expectedTurnId)
    {
        if (expectedTurnId is null)
        {
            return true;
        }

        return string.Equals(ReadString(parameters, "turnId"), expectedTurnId, StringComparison.Ordinal);
    }

    private static string ReadErrorMessage(JsonElement parameters)
    {
        if (!TryGetProperty(parameters, "error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return EmptyErrorMessage;
        }

        return ReadString(error, "message") ?? EmptyErrorMessage;
    }

    private static CommandExecutionStatus MapCommandStatus(string? status) => status switch
    {
        "inProgress" => CommandExecutionStatus.InProgress,
        "completed" => CommandExecutionStatus.Completed,
        _ => CommandExecutionStatus.Failed
    };

    private static PatchApplyStatus MapPatchStatus(string? status) => status switch
    {
        "inProgress" => PatchApplyStatus.InProgress,
        "completed" => PatchApplyStatus.Completed,
        _ => PatchApplyStatus.Failed
    };

    private static McpToolCallStatus MapMcpStatus(string? status) => status switch
    {
        "inProgress" => McpToolCallStatus.InProgress,
        "completed" => McpToolCallStatus.Completed,
        _ => McpToolCallStatus.Failed
    };

    private static IReadOnlyList<FileUpdateChange> ReadChanges(JsonElement item)
    {
        if (!TryGetProperty(item, "changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<FileUpdateChange>();
        foreach (var change in changes.EnumerateArray())
        {
            var path = ReadString(change, "path") ?? string.Empty;
            var kind = TryGetProperty(change, "kind", out var kindElement) ? ReadString(kindElement, "type") : null;
            var patchKind = kind switch
            {
                "add" => PatchChangeKind.Add,
                "delete" => PatchChangeKind.Delete,
                _ => PatchChangeKind.Update
            };
            result.Add(new FileUpdateChange(path, patchKind));
        }

        return result;
    }

    private static McpToolCallResult? ReadMcpResult(JsonElement item)
    {
        if (!TryGetProperty(item, "result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var content = new List<JsonNode>();
        if (TryGetProperty(result, "content", out var contentElement) && contentElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in contentElement.EnumerateArray())
            {
                if (JsonNode.Parse(entry.GetRawText()) is { } node)
                {
                    content.Add(node);
                }
            }
        }

        return new McpToolCallResult(content, ReadNode(result, "structuredContent"));
    }

    private static McpToolCallError? ReadMcpError(JsonElement item)
    {
        var message = ReadErrorMessage(item);
        return string.IsNullOrEmpty(message) ? null : new McpToolCallError(message);
    }

    private static string JoinStrings(JsonElement item, params string[] properties)
    {
        var builder = new StringBuilder();
        foreach (var property in properties)
        {
            if (!TryGetProperty(item, property, out var array) || array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(entry.GetString());
            }
        }

        return builder.ToString();
    }

    private static JsonNode? ReadNode(JsonElement element, string property)
    {
        return TryGetProperty(element, property, out var value) && value.ValueKind != JsonValueKind.Null
            ? JsonNode.Parse(value.GetRawText())
            : null;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        return TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
    }

    private static long ReadLong(JsonElement element, string property)
    {
        return TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : 0;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out value))
        {
            return true;
        }

        value = default;
        return false;
    }
}
