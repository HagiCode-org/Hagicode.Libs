using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Hermes;

internal static class HermesAcpMessageMapper
{
    private static readonly string[] FailureSignalTokens =
    [
        "403",
        "forbidden",
        "permission denied",
        "permission-denied",
        "permission_denied",
        "access denied",
        "unauthorized",
        "authentication failed",
        "auth failed"
    ];

    public static CliMessage CreateSessionLifecycleMessage(AcpSessionHandle sessionHandle)
    {
        return CreateSessionLifecycleMessage(
            sessionHandle.SessionId,
            sessionHandle.IsResumed ? "session.resumed" : "session.started");
    }

    public static CliMessage CreateSessionReusedMessage(string sessionId, string? requestedKey = null)
    {
        return new CliMessage(
            "session.reused",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "session.reused",
                ["session_id"] = sessionId,
                ["reuse_key"] = requestedKey
            }));
    }

    private static CliMessage CreateSessionLifecycleMessage(string sessionId, string messageType)
    {
        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId
            }));
    }

    public static CliMessage CreateTerminalMessage(string sessionId, JsonElement promptResult)
    {
        var stopReason = TryGetPromptResultStopReason(promptResult);
        var isFailure = TryExtractFailureSummary(promptResult, stopReason, out var failureSummary);
        var text = isFailure
            ? failureSummary
            : TryExtractPromptResultText(promptResult, out var terminalText)
                ? terminalText
                : null;

        return CreateTerminalPayload(
            sessionId,
            promptResult,
            "result",
            stopReason,
            isFailure,
            text);
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
        if (!parameters.TryGetProperty("update", out var updateElement) || updateElement.ValueKind != JsonValueKind.Object)
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

        var updateKind = TryGetString(updateElement, "sessionUpdate") ?? "unknown";
        if (TryCreateTerminalFailureUpdateMessage(sessionId, updateKind, updateElement, out var failureMessage))
        {
            return [failureMessage!];
        }

        return updateKind switch
        {
            "agent_message_chunk" => [CreateAssistantUpdateMessage(sessionId, updateElement, "assistant")],
            "agent_thought_chunk" => [],
            "tool_call" => [CreateUpdateMessage("tool.call", sessionId, updateElement)],
            "tool_call_update" => [CreateUpdateMessage("tool.update", sessionId, updateElement)],
            "prompt_completed" => [CreatePromptCompletedMessage(sessionId, updateElement)],
            _ =>
            [
                CreateUpdateMessage("session.update", sessionId, updateElement)
            ]
        };
    }

    public static bool ShouldPreferPromptCompletedNotification(JsonElement promptResult)
    {
        var stopReason = TryGetPromptResultStopReason(promptResult);
        return !IsFailurePromptResult(promptResult) &&
               (string.Equals(stopReason, "end_turn", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "success", StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsFailurePromptResult(JsonElement promptResult)
    {
        var stopReason = TryGetPromptResultStopReason(promptResult);
        return TryExtractFailureSummary(promptResult, stopReason, out _);
    }

    public static bool TryExtractPromptResultText(JsonElement promptResult, out string? text)
    {
        text = null;
        if (ProviderResponseTextFidelity.TryGetText(promptResult, out text, "outputText", "text"))
        {
            return true;
        }

        if (promptResult.ValueKind == JsonValueKind.Object)
        {
            if (promptResult.TryGetProperty("content", out var contentElement))
            {
                text = ExtractTextFromContent(contentElement);
            }
            else if (promptResult.TryGetProperty("message", out var messageElement))
            {
                text = ExtractTextFromContent(messageElement);
            }
        }

        return ProviderResponseTextFidelity.HasText(text);
    }

    public static bool TryExtractMessageText(JsonElement content, out string? text)
    {
        text = null;
        return content.ValueKind == JsonValueKind.Object &&
               ProviderResponseTextFidelity.TryGetText(content, out text, "text");
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
        var stopReason = TryGetString(updateElement, "stopReason");
        var isFailure = TryExtractFailureSummary(updateElement, stopReason, out var failureSummary);
        var text = isFailure ? failureSummary : ExtractText(updateElement);

        return CreateTerminalPayload(
            sessionId,
            updateElement,
            "update",
            stopReason,
            isFailure,
            text);
    }

    private static bool TryCreateTerminalFailureUpdateMessage(
        string sessionId,
        string updateKind,
        JsonElement updateElement,
        out CliMessage? message)
    {
        message = null;
        if (IsNonTerminalUpdateKind(updateKind))
        {
            return false;
        }

        var stopReason = TryGetString(updateElement, "stopReason") ?? TryGetString(updateElement, "status");
        string? failureSummary = null;
        if (!LooksLikeFailureUpdateKind(updateKind) &&
            !TryExtractFailureSummary(updateElement, stopReason, out failureSummary))
        {
            return false;
        }

        if (failureSummary is null &&
            !TryExtractFailureSummary(updateElement, stopReason, out failureSummary))
        {
            failureSummary = NormalizeOptional(stopReason) ?? updateKind;
        }

        message = CreateTerminalPayload(
            sessionId,
            updateElement,
            "update",
            stopReason,
            isFailure: true,
            failureSummary);
        return true;
    }

    private static CliMessage CreateTerminalPayload(
        string sessionId,
        JsonElement rawPayload,
        string payloadPropertyName,
        string? stopReason,
        bool isFailure,
        string? text)
    {
        var messageType = isFailure ? "terminal.failed" : "terminal.completed";
        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId,
                ["stop_reason"] = stopReason,
                ["text"] = text,
                ["message"] = isFailure ? text : null,
                [payloadPropertyName] = rawPayload
            }));
    }

    private static string? ExtractText(JsonElement updateElement)
    {
        if (!updateElement.TryGetProperty("content", out var contentElement))
        {
            ProviderResponseTextFidelity.TryGetText(updateElement, out var directText, "text", "message");
            return directText;
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
        if (ProviderResponseTextFidelity.TryGetText(contentElement, out var directText, "text"))
        {
            return directText;
        }

        if (ProviderResponseTextFidelity.TryGetText(contentElement, out var directMessage, "message"))
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
            if (ProviderResponseTextFidelity.HasText(text))
            {
                parts.Add(text!);
            }
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static string? TryGetPromptResultStopReason(JsonElement promptResult)
    {
        return TryGetString(promptResult, "stopReason") ?? TryGetString(promptResult, "status");
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }

    private static bool TryExtractFailureSummary(JsonElement payload, string? stopReason, out string? summary)
    {
        summary = null;
        foreach (var candidate in EnumerateFailureCandidates(payload))
        {
            if (LooksLikeFailureText(candidate))
            {
                summary = candidate;
                return true;
            }
        }

        if (IsFailureStopReason(stopReason))
        {
            summary = NormalizeOptional(stopReason) ?? "Provider execution failed.";
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateFailureCandidates(JsonElement payload)
    {
        if (TryGetString(payload, "message") is { } messageText)
        {
            yield return messageText;
        }

        if (TryGetString(payload, "error_message") is { } errorMessage)
        {
            yield return errorMessage;
        }

        if (TryGetString(payload, "detail") is { } detail)
        {
            yield return detail;
        }

        if (TryGetString(payload, "details") is { } details)
        {
            yield return details;
        }

        if (TryExtractPromptResultText(payload, out var promptText) && promptText is not null)
        {
            yield return promptText;
        }

        foreach (var propertyName in new[] { "error", "result", "update", "diagnostic", "diagnostics" })
        {
            if (!payload.TryGetProperty(propertyName, out var nestedElement))
            {
                continue;
            }

            foreach (var nested in EnumerateNestedFailureCandidates(nestedElement))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<string> EnumerateNestedFailureCandidates(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                if (NormalizeOptional(element.GetString()) is { } stringValue)
                {
                    yield return stringValue;
                }

                yield break;
            case JsonValueKind.Object:
                foreach (var propertyName in new[] { "message", "text", "error_message", "detail", "details", "status", "stopReason", "stop_reason" })
                {
                    if (TryGetString(element, propertyName) is { } value)
                    {
                        yield return value;
                    }
                }

                if (element.TryGetProperty("content", out var nestedContent))
                {
                    foreach (var nested in EnumerateNestedFailureCandidates(nestedContent))
                    {
                        yield return nested;
                    }
                }

                if (element.TryGetProperty("error", out var nestedError))
                {
                    foreach (var nested in EnumerateNestedFailureCandidates(nestedError))
                    {
                        yield return nested;
                    }
                }

                yield break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in EnumerateNestedFailureCandidates(item))
                    {
                        yield return nested;
                    }
                }

                yield break;
        }
    }

    private static bool IsNonTerminalUpdateKind(string updateKind)
    {
        return string.Equals(updateKind, "agent_message_chunk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(updateKind, "agent_thought_chunk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(updateKind, "tool_call", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(updateKind, "tool_call_update", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(updateKind, "prompt_completed", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFailureUpdateKind(string updateKind)
    {
        if (string.IsNullOrWhiteSpace(updateKind))
        {
            return false;
        }

        var normalized = updateKind.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);

        return normalized.Contains("error", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("forbidden", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("permissiondenied", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeFailureText(string? text)
    {
        var normalized = NormalizeOptional(text);
        if (normalized is null)
        {
            return false;
        }

        foreach (var token in FailureSignalTokens)
        {
            if (normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFailureStopReason(string? stopReason)
    {
        var normalized = NormalizeOptional(stopReason);
        return string.Equals(normalized, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "failure", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "cancelled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "canceled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "forbidden", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "permission-denied", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "permission_denied", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalized, "unauthorized", StringComparison.OrdinalIgnoreCase) ||
               LooksLikeFailureText(normalized);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
