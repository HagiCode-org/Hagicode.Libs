using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Kimi;

internal static class KimiAcpMessageMapper
{
    public static CliMessage CreateSessionLifecycleMessage(AcpSessionHandle sessionHandle)
    {
        return new CliMessage(
            sessionHandle.IsResumed ? "session.resumed" : "session.started",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = sessionHandle.IsResumed ? "session.resumed" : "session.started",
                ["session_id"] = sessionHandle.SessionId
            }));
    }

    public static CliMessage CreateTerminalMessage(string sessionId, JsonElement promptResult)
    {
        var stopReason = TryGetPromptResultStopReason(promptResult);
        var messageType = IsFailureStopReason(stopReason) ? "terminal.failed" : "terminal.completed";

        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId,
                ["stop_reason"] = stopReason,
                ["text"] = TryExtractPromptResultText(promptResult, out var text) ? text : null,
                ["result"] = promptResult
            }));
    }

    public static CliMessage CreateAssistantMessage(string sessionId, string? text, JsonElement? rawPayload = null)
    {
        return new CliMessage(
            "assistant",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "assistant",
                ["session_id"] = sessionId,
                ["text"] = text,
                ["update"] = rawPayload
            }));
    }

    public static CliMessage CreateTerminalFailureMessage(string sessionId, Exception exception)
    {
        return new CliMessage(
            "terminal.failed",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "terminal.failed",
                ["session_id"] = sessionId,
                ["message"] = exception.Message
            }));
    }

    public static IReadOnlyList<CliMessage> NormalizeNotification(AcpNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return
            [
                new CliMessage(
                    "session.notification",
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["type"] = "session.notification",
                        ["method"] = notification.Method,
                        ["params"] = notification.Parameters
                    }))
            ];
        }

        var parameters = notification.Parameters;
        var sessionId = TryGetString(parameters, "sessionId") ?? string.Empty;
        var updateElement = ResolveUpdateElement(parameters);
        if (updateElement is null)
        {
            return
            [
                new CliMessage(
                    "session.update",
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?>
                    {
                        ["type"] = "session.update",
                        ["session_id"] = sessionId,
                        ["update"] = parameters
                    }))
            ];
        }

        var updateKind = TryGetUpdateKind(updateElement.Value) ?? "unknown";
        return updateKind switch
        {
            "assistant" or "assistant_message" or "agent_message_chunk" => [CreateAssistantUpdateMessage(sessionId, updateElement.Value, "assistant")],
            "thought" or "assistant_thought" or "agent_thought_chunk" => [CreateAssistantUpdateMessage(sessionId, updateElement.Value, "assistant.thought")],
            "tool_call" => [CreateUpdateMessage("tool.call", sessionId, updateElement.Value)],
            "tool_call_update" => [CreateUpdateMessage("tool.update", sessionId, updateElement.Value)],
            "error" => [CreateErrorMessage(sessionId, updateElement.Value)],
            "prompt_completed" or "completed" => [CreatePromptCompletedMessage(sessionId, updateElement.Value)],
            _ =>
            [
                CreateUpdateMessage("session.update", sessionId, updateElement.Value)
            ]
        };
    }

    public static bool ShouldPreferPromptCompletedNotification(JsonElement promptResult)
    {
        var stopReason = TryGetPromptResultStopReason(promptResult);
        return string.Equals(stopReason, "end_turn", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "success", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFailurePromptResult(JsonElement promptResult)
    {
        return IsFailureStopReason(TryGetPromptResultStopReason(promptResult));
    }

    public static bool TryExtractPromptResultText(JsonElement promptResult, out string? text)
    {
        text = TryGetString(promptResult, "outputText") ?? TryGetString(promptResult, "text");
        if (!string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (promptResult.ValueKind == JsonValueKind.Object &&
            promptResult.TryGetProperty("content", out var contentElement))
        {
            text = ExtractTextFromContent(contentElement);
        }

        return !string.IsNullOrWhiteSpace(text);
    }

    public static bool TryExtractMessageText(JsonElement content, out string? text)
    {
        text = null;
        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = textElement.GetString();
        return !string.IsNullOrWhiteSpace(text);
    }

    public static bool IsReplayAssistantNotification(AcpNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!notification.Parameters.TryGetProperty("_meta", out var metaElement) ||
            metaElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var updateElement = ResolveUpdateElement(notification.Parameters);
        if (updateElement is null ||
            !string.Equals(TryGetUpdateKind(updateElement.Value), "agent_message_chunk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var streamed = TryGetBoolean(metaElement, "ai-coding/streamed");
        var requestId = TryGetString(metaElement, "ai-coding/request-id");
        var turnId = TryGetString(metaElement, "ai-coding/turn-id");

        return streamed == false &&
               string.IsNullOrWhiteSpace(requestId) &&
               string.IsNullOrWhiteSpace(turnId);
    }

    private static JsonElement? ResolveUpdateElement(JsonElement parameters)
    {
        if (parameters.TryGetProperty("update", out var updateElement) && updateElement.ValueKind == JsonValueKind.Object)
        {
            return updateElement;
        }

        if (parameters.TryGetProperty("delta", out var deltaElement) && deltaElement.ValueKind == JsonValueKind.Object)
        {
            return deltaElement;
        }

        return parameters.ValueKind == JsonValueKind.Object ? parameters : null;
    }

    private static string? TryGetUpdateKind(JsonElement updateElement)
    {
        return TryGetString(updateElement, "kind") ??
               TryGetString(updateElement, "sessionUpdate") ??
               TryGetString(updateElement, "type");
    }

    private static CliMessage CreateAssistantUpdateMessage(string sessionId, JsonElement updateElement, string messageType)
    {
        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId,
                ["text"] = ExtractText(updateElement),
                ["update"] = updateElement
            }));
    }

    private static CliMessage CreateUpdateMessage(string messageType, string sessionId, JsonElement updateElement)
    {
        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId,
                ["update"] = updateElement
            }));
    }

    private static CliMessage CreatePromptCompletedMessage(string sessionId, JsonElement updateElement)
    {
        var stopReason = TryGetString(updateElement, "stopReason") ?? TryGetString(updateElement, "status");
        var messageType = IsFailureStopReason(stopReason) ? "terminal.failed" : "terminal.completed";
        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId,
                ["stop_reason"] = stopReason,
                ["text"] = ExtractText(updateElement),
                ["update"] = updateElement
            }));
    }

    private static CliMessage CreateErrorMessage(string sessionId, JsonElement updateElement)
    {
        return new CliMessage(
            "terminal.failed",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "terminal.failed",
                ["session_id"] = sessionId,
                ["message"] = TryGetString(updateElement, "message") ?? ExtractText(updateElement),
                ["update"] = updateElement
            }));
    }

    private static string? ExtractText(JsonElement updateElement)
    {
        if (!updateElement.TryGetProperty("content", out var contentElement))
        {
            return TryGetString(updateElement, "text") ?? TryGetString(updateElement, "message");
        }

        return ExtractTextFromContent(contentElement);
    }

    private static string? ExtractTextFromContent(JsonElement contentElement)
    {
        return contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString(),
            JsonValueKind.Object => ExtractTextFromObject(contentElement),
            JsonValueKind.Array => ExtractTextFromArray(contentElement),
            _ => null
        };
    }

    private static string? ExtractTextFromObject(JsonElement contentElement)
    {
        if (TryGetString(contentElement, "text") is { Length: > 0 } directText)
        {
            return directText;
        }

        if (TryGetString(contentElement, "message") is { Length: > 0 } directMessage)
        {
            return directMessage;
        }

        if (contentElement.TryGetProperty("content", out var nestedContent))
        {
            return ExtractTextFromContent(nestedContent);
        }

        return null;
    }

    private static string? ExtractTextFromArray(JsonElement contentElement)
    {
        var parts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            var text = ExtractTextFromContent(item);
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static string? TryGetPromptResultStopReason(JsonElement promptResult)
    {
        return TryGetString(promptResult, "stopReason") ?? TryGetString(promptResult, "status");
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               (propertyElement.ValueKind == JsonValueKind.True || propertyElement.ValueKind == JsonValueKind.False)
            ? propertyElement.GetBoolean()
            : null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static bool IsFailureStopReason(string? stopReason)
    {
        return string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "rejected", StringComparison.OrdinalIgnoreCase);
    }
}
