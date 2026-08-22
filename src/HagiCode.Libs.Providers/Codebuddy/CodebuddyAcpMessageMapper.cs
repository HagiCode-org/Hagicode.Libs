using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Codebuddy;

internal static class CodebuddyAcpMessageMapper
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
        return updateKind switch
        {
            "agent_message_chunk" => [CreateAssistantUpdateMessage(sessionId, updateElement, "assistant")],
            "agent_thought_chunk" => [CreateAssistantUpdateMessage(sessionId, updateElement, "assistant.thought")],
            "tool_call" => [CreateUpdateMessage("tool.call", sessionId, updateElement)],
            "tool_call_update" => [CreateUpdateMessage("tool.update", sessionId, updateElement)],
            "prompt_completed" => [
                CreatePromptCompletedMessage(
                    sessionId,
                    updateElement,
                    parameters.TryGetProperty("result", out var promptResult) ? promptResult : null)
            ],
            _ =>
            [
                CreateUpdateMessage("session.update", sessionId, updateElement)
            ]
        };
    }

    /// <summary>
    /// Detects the ACP <c>session_info_update</c> markers the CodeBuddy CLI emits to
    /// bracket a resumed session's history replay
    /// (<c>codebuddy.ai/historyReplay: "start"</c> / <c>"end"</c>).
    /// </summary>
    /// <param name="notification">The inbound ACP notification.</param>
    /// <param name="isStart">Set to <c>true</c> for the start marker, <c>false</c> for the end marker.</param>
    /// <returns><c>true</c> when the notification is a replay-window boundary.</returns>
    public static bool TryGetReplayWindowBoundary(AcpNotification notification, out bool isStart)
    {
        isStart = false;
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!notification.Parameters.TryGetProperty("update", out var updateElement) ||
            updateElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!updateElement.TryGetProperty("sessionUpdate", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String ||
            !string.Equals(kindElement.GetString(), "session_info_update", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!updateElement.TryGetProperty("_meta", out var metaElement) ||
            metaElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if ((!metaElement.TryGetProperty("codebuddy.ai/historyReplay", out var replayElement) &&
             !metaElement.TryGetProperty("historyReplay", out replayElement)) ||
            replayElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = replayElement.GetString();
        if (string.Equals(value, "start", StringComparison.OrdinalIgnoreCase))
        {
            isStart = true;
            return true;
        }

        if (string.Equals(value, "end", StringComparison.OrdinalIgnoreCase))
        {
            isStart = false;
            return true;
        }

        return false;
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
        text = null;
        if (IsFailurePromptResult(promptResult) &&
            promptResult.ValueKind == JsonValueKind.Object &&
            promptResult.TryGetProperty("errors", out var failureErrorsElement))
        {
            text = ExtractTextFromContent(failureErrorsElement);
            if (ProviderResponseTextFidelity.HasText(text))
            {
                return true;
            }
        }

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
            else if (promptResult.TryGetProperty("errors", out var errorsElement))
            {
                text = ExtractTextFromContent(errorsElement);
            }
            else if (promptResult.TryGetProperty("result", out var resultElement))
            {
                text = ExtractTextFromContent(resultElement);
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

    private static CliMessage CreatePromptCompletedMessage(
        string sessionId,
        JsonElement updateElement,
        JsonElement? promptResult = null)
    {
        var stopReason = TryGetString(updateElement, "stopReason");
        if (promptResult is { } result && IsFailurePromptResult(result))
        {
            return CreateTerminalMessage(sessionId, result);
        }

        var messageType = IsFailureStopReason(stopReason) ? "terminal.failed" : "terminal.completed";
        return new CliMessage(
            messageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = messageType,
                ["session_id"] = sessionId,
                ["stop_reason"] = stopReason,
                ["text"] = ExtractText(updateElement),
                ["update"] = updateElement,
                ["result"] = promptResult
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
            JsonValueKind.String => ExtractTextFromEncodedString(contentElement),
            JsonValueKind.Object => ExtractTextFromObject(contentElement),
            JsonValueKind.Array => ExtractTextFromArray(contentElement),
            _ => null
        };
    }

    /// <summary>
    /// The CodeBuddy CLI may deliver chunk content as a JSON-encoded string
    /// (e.g. "{\"type\":\"text\",\"text\":\"Hello\"}") instead of a plain text delta.
    /// Decode the envelope so the underlying text is used; otherwise the raw JSON
    /// envelope would leak into the stream and break the incremental-delta and
    /// resume-replay dedup that rely on real text.
    /// </summary>
    private static string? ExtractTextFromEncodedString(JsonElement contentElement)
    {
        var raw = contentElement.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return raw;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var decoded = document.RootElement.ValueKind switch
            {
                JsonValueKind.Object => ExtractTextFromObject(document.RootElement),
                JsonValueKind.Array => ExtractTextFromArray(document.RootElement),
                _ => null
            };

            return string.IsNullOrEmpty(decoded) ? raw : decoded;
        }
        catch (JsonException)
        {
            // Not a JSON envelope; treat the raw string as the text.
            return raw;
        }
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

        if (contentElement.TryGetProperty("errors", out var errors))
        {
            return ExtractTextFromContent(errors);
        }

        if (contentElement.TryGetProperty("errors_info", out var errorsInfo))
        {
            return ExtractTextFromContent(errorsInfo);
        }

        if (contentElement.TryGetProperty("result", out var result))
        {
            return ExtractTextFromContent(result);
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
        if (IsFailureResult(promptResult))
        {
            return "error";
        }

        return TryGetString(promptResult, "stopReason")
            ?? TryGetString(promptResult, "status")
            ?? (promptResult.ValueKind == JsonValueKind.Object &&
                promptResult.TryGetProperty("result", out var result)
                    ? TryGetPromptResultStopReason(result)
                    : null);
    }

    private static bool IsFailureResult(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (element.TryGetProperty("is_error", out var isError) &&
            isError.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        var subtype = TryGetString(element, "subtype");
        if (subtype?.Contains("error", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (element.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            return true;
        }

        if (element.TryGetProperty("errors_info", out var errorsInfo) &&
            errorsInfo.ValueKind == JsonValueKind.Array &&
            errorsInfo.GetArrayLength() > 0)
        {
            return true;
        }

        return element.TryGetProperty("result", out var result) && IsFailureResult(result);
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
               string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase);
    }
}