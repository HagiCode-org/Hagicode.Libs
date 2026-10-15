using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Dsh;

internal static class DshAcpMessageMapper
{
    public static CliMessage CreateSessionLifecycleMessage(AcpSessionHandle sessionHandle)
    {
        var messageType = sessionHandle.IsResumed ? "session.resumed" : "session.started";
        return Create(messageType, new Dictionary<string, object?> { ["session_id"] = sessionHandle.SessionId });
    }

    public static CliMessage CreateTerminalMessage(string sessionId, JsonElement promptResult)
    {
        var stopReason = TryGetString(promptResult, "stopReason") ?? TryGetString(promptResult, "status");
        return CreateTerminal(sessionId, stopReason, ExtractText(promptResult), promptResult);
    }

    public static CliMessage CreateAssistantMessage(string sessionId, string text, JsonElement rawPayload)
    {
        return Create("assistant", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["text"] = text,
            ["update"] = rawPayload
        });
    }

    public static CliMessage CreateTerminalFailureMessage(string sessionId, string message)
    {
        return Create("terminal.failed", new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["message"] = message
        });
    }

    public static bool IsSuccessfulStopReason(JsonElement promptResult)
    {
        var stopReason = TryGetString(promptResult, "stopReason") ?? TryGetString(promptResult, "status");
        return string.Equals(stopReason, "end_turn", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "success", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryGetResultText(JsonElement promptResult, out string? text)
    {
        text = ExtractText(promptResult);
        return !string.IsNullOrEmpty(text);
    }

    public static IReadOnlyList<CliMessage> NormalizeNotification(AcpNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return
            [
                Create("session.notification", new Dictionary<string, object?>
                {
                    ["method"] = notification.Method,
                    ["params"] = notification.Parameters
                })
            ];
        }

        var parameters = notification.Parameters;
        var sessionId = TryGetString(parameters, "sessionId") ?? string.Empty;
        var update = parameters.TryGetProperty("update", out var updateElement) && updateElement.ValueKind == JsonValueKind.Object
            ? updateElement
            : parameters;
        var updateKind = TryGetString(update, "sessionUpdate") ?? TryGetString(update, "kind") ?? "unknown";

        return updateKind switch
        {
            "agent_message_chunk" or "assistant" => [CreateUpdate("assistant", sessionId, update, ExtractText(update))],
            "agent_thought_chunk" or "thought" => [CreateUpdate("assistant.thought", sessionId, update, ExtractText(update))],
            "tool_call" => [CreateUpdate("tool.call", sessionId, AcpToolKindAnnotator.Annotate(update))],
            "tool_call_update" => [CreateUpdate("tool.update", sessionId, AcpToolKindAnnotator.Annotate(update))],
            "error" => [CreateTerminalFailureMessage(sessionId, TryGetString(update, "message") ?? ExtractText(update) ?? "dsh reported an error.")],
            "prompt_completed" => [CreateTerminal(sessionId, TryGetString(update, "stopReason"), ExtractText(update) ?? ExtractResultText(parameters), update)],
            _ => [CreateUpdate("session.update", sessionId, update)]
        };
    }

    private static CliMessage CreateTerminal(string sessionId, string? stopReason, string? text, JsonElement rawPayload)
    {
        var messageType = IsFailureStopReason(stopReason) ? "terminal.failed" : "terminal.completed";
        return Create(messageType, new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["stop_reason"] = stopReason,
            ["text"] = text,
            ["result"] = rawPayload
        });
    }

    private static CliMessage CreateUpdate(string messageType, string sessionId, JsonElement update, string? text = null)
    {
        return Create(messageType, new Dictionary<string, object?>
        {
            ["session_id"] = sessionId,
            ["text"] = text,
            ["update"] = update
        });
    }

    private static CliMessage Create(string messageType, Dictionary<string, object?> payload)
    {
        payload["type"] = messageType;
        return new CliMessage(messageType, JsonSerializer.SerializeToElement(payload));
    }

    private static string? ExtractText(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(element, "text") ??
               TryGetString(element, "message") ??
               (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object
                   ? TryGetString(content, "text")
                   : null);
    }

    private static string? ExtractResultText(JsonElement parameters)
    {
        return parameters.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
            ? ExtractText(result)
            : null;
    }

    private static bool IsFailureStopReason(string? stopReason)
    {
        return string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "rejected", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }
}
