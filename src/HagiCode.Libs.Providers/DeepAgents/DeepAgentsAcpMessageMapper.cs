using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Kimi;

namespace HagiCode.Libs.Providers.DeepAgents;

internal static class DeepAgentsAcpMessageMapper
{
    public static CliMessage CreateSessionLifecycleMessage(AcpSessionHandle sessionHandle)
        => KimiAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

    public static CliMessage CreateTerminalMessage(string sessionId, JsonElement promptResult)
        => KimiAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);

    public static CliMessage CreateAssistantMessage(string sessionId, string? text, JsonElement? rawPayload = null)
        => KimiAcpMessageMapper.CreateAssistantMessage(sessionId, text, rawPayload);

    public static CliMessage CreateTerminalFailureMessage(string sessionId, Exception exception)
        => KimiAcpMessageMapper.CreateTerminalFailureMessage(sessionId, exception);

    public static IReadOnlyList<CliMessage> NormalizeNotification(AcpNotification notification)
    {
        if (string.Equals(notification.Method, "session/request_permission", StringComparison.OrdinalIgnoreCase) &&
            notification.Parameters.ValueKind == JsonValueKind.Object)
        {
            return [CreatePermissionRequestMessage(notification.Parameters)];
        }

        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return KimiAcpMessageMapper.NormalizeNotification(notification);
        }

        var parameters = notification.Parameters;
        var sessionId = TryGetString(parameters, "sessionId") ?? string.Empty;
        var updateElement = ResolveUpdateElement(parameters);
        if (updateElement is null)
        {
            return KimiAcpMessageMapper.NormalizeNotification(notification);
        }

        var updateKind = TryGetUpdateKind(updateElement.Value);
        return updateKind switch
        {
            "tool_call" => [CreateToolLifecycleMessage("tool.call", sessionId, updateElement.Value)],
            "tool_call_update" => [CreateToolLifecycleMessage(ResolveToolMessageType(updateElement.Value) ?? "tool.update", sessionId, updateElement.Value)],
            _ => KimiAcpMessageMapper.NormalizeNotification(notification)
        };
    }

    public static bool ShouldPreferPromptCompletedNotification(JsonElement promptResult)
        => KimiAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult);

    public static bool IsFailurePromptResult(JsonElement promptResult)
        => KimiAcpMessageMapper.IsFailurePromptResult(promptResult);

    public static bool TryExtractPromptResultText(JsonElement promptResult, out string? text)
        => KimiAcpMessageMapper.TryExtractPromptResultText(promptResult, out text);

    public static bool TryExtractMessageText(JsonElement content, out string? text)
        => KimiAcpMessageMapper.TryExtractMessageText(content, out text);

    public static bool IsReplayAssistantNotification(AcpNotification notification)
        => KimiAcpMessageMapper.IsReplayAssistantNotification(notification);

    private static CliMessage CreatePermissionRequestMessage(JsonElement parameters)
    {
        var toolCall = parameters.TryGetProperty("toolCall", out var toolCallElement) &&
                       toolCallElement.ValueKind == JsonValueKind.Object
            ? toolCallElement
            : default;

        return new CliMessage(
            "tool.permission",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "tool.permission",
                ["session_id"] = TryGetString(parameters, "sessionId"),
                ["tool_call_id"] = TryGetString(toolCall, "toolCallId"),
                ["title"] = TryGetString(toolCall, "title"),
                ["options"] = parameters.TryGetProperty("options", out var optionsElement) ? optionsElement : default,
                ["request"] = parameters
            }));
    }

    private static CliMessage CreateToolLifecycleMessage(string messageType, string sessionId, JsonElement updateElement)
    {
        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId,
                ["tool_name"] = TryGetToolName(updateElement),
                ["tool_call_id"] = TryGetToolCallId(updateElement),
                ["status"] = TryGetString(updateElement, "status") ?? TryGetString(updateElement, "state"),
                ["text"] = TryGetString(updateElement, "message") ?? TryGetString(updateElement, "text"),
                ["update"] = updateElement
            }));
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

    private static string? ResolveToolMessageType(JsonElement updateElement)
    {
        var status = TryGetString(updateElement, "status")
                     ?? TryGetString(updateElement, "state")
                     ?? TryGetString(updateElement, "phase");
        if (status is null)
        {
            return null;
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "completed" or "complete" or "done" or "success" or "succeeded" => "tool.completed",
            "failed" or "failure" or "error" => "tool.failed",
            _ => null
        };
    }

    private static string? TryGetUpdateKind(JsonElement updateElement)
    {
        return TryGetString(updateElement, "sessionUpdate") ??
               TryGetString(updateElement, "kind") ??
               TryGetString(updateElement, "type");
    }

    private static string? TryGetToolName(JsonElement updateElement)
    {
        return TryGetString(updateElement, "toolName") ??
               TryGetString(updateElement, "tool_name") ??
               TryGetString(updateElement, "name") ??
               TryGetToolKind(updateElement) ??
               TryGetNestedString(updateElement, "tool", "name") ??
               TryGetNestedString(updateElement, "toolCall", "name");
    }

    private static string? TryGetToolKind(JsonElement updateElement)
    {
        var value = TryGetString(updateElement, "kind") ??
                    TryGetString(updateElement, "type");
        if (string.IsNullOrWhiteSpace(value) ||
            string.Equals(value, "tool_call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "tool_call_update", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value;
    }

    private static string? TryGetToolCallId(JsonElement updateElement)
    {
        return TryGetString(updateElement, "toolCallId") ??
               TryGetString(updateElement, "tool_call_id") ??
               TryGetString(updateElement, "callId") ??
               TryGetString(updateElement, "id") ??
               TryGetNestedString(updateElement, "tool", "id") ??
               TryGetNestedString(updateElement, "toolCall", "id");
    }

    private static string? TryGetNestedString(JsonElement element, string propertyName, string nestedPropertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return TryGetString(propertyElement, nestedPropertyName);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var propertyElement))
        {
            return null;
        }

        return propertyElement.ValueKind switch
        {
            JsonValueKind.String => propertyElement.GetString(),
            JsonValueKind.Number => propertyElement.GetRawText(),
            _ => null
        };
    }
}
