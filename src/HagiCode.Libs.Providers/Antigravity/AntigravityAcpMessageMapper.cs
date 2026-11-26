using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Antigravity;

internal static class AntigravityAcpMessageMapper
{
    /// <summary>
    /// The phrase the CLI streams as assistant text when the signed-in account has no model entitlement (#3501).
    /// Matching is narrow on purpose: only this phrase, case-insensitively, is treated as a license rejection.
    /// </summary>
    internal const string LicenseRejectionPhrase = "do not have a valid license";

    /// <summary>The failure message surfaced for a license rejection instead of the CLI text.</summary>
    internal const string LicenseRejectionMessage =
        "Antigravity CLI reports this account has no valid license (#3501). Check the account's Antigravity entitlement and sign in again.";

    public static bool ContainsLicenseRejection(string? text)
    {
        return !string.IsNullOrEmpty(text) &&
               text.Contains(LicenseRejectionPhrase, StringComparison.OrdinalIgnoreCase);
    }

    public static CliMessage CreateLicenseRejectedMessage(string sessionId, string? cliText)
    {
        return new CliMessage(
            "terminal.failed",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "terminal.failed",
                ["session_id"] = sessionId,
                ["stop_reason"] = "error",
                ["message"] = LicenseRejectionMessage,
                ["text"] = cliText
            }));
    }

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
            "tool_call" => CreateToolCallMessages("tool.call", sessionId, updateElement.Value),
            "tool_call_update" => CreateToolCallMessages("tool.update", sessionId, updateElement.Value),
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
        text = null;
        if (TryGetMeaningfulText(promptResult, out text, "outputText", "text"))
        {
            return true;
        }

        if (promptResult.ValueKind == JsonValueKind.Object &&
            promptResult.TryGetProperty("content", out var contentElement))
        {
            text = ExtractTextFromContent(contentElement);
        }

        return HasMeaningfulText(text);
    }

    public static bool TryExtractMessageText(JsonElement content, out string? text)
    {
        text = null;
        if (content.ValueKind != JsonValueKind.Object ||
            !TryGetMeaningfulText(content, out text, "text"))
        {
            return false;
        }

        return true;
    }

    public static bool TryExtractNotificationResultText(AcpNotification notification, out string? text)
    {
        text = null;
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object ||
            !notification.Parameters.TryGetProperty("result", out var resultElement) ||
            resultElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryExtractPromptResultText(resultElement, out text);
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
        return TryGetString(updateElement, "sessionUpdate") ??
               TryGetString(updateElement, "kind") ??
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

    // An ask_question tool call also yields a user.question message, so the caller can see the question in stream order. The
    // tool call itself is still passed on unchanged.
    private static IReadOnlyList<CliMessage> CreateToolCallMessages(string messageType, string sessionId, JsonElement updateElement)
    {
        var messages = new List<CliMessage> { CreateUpdateMessage(messageType, sessionId, AcpToolKindAnnotator.Annotate(updateElement)) };
        if (TryCreateUserQuestionMessage(sessionId, updateElement, out var questionMessage))
        {
            messages.Add(questionMessage);
        }

        return messages;
    }

    private static bool TryCreateUserQuestionMessage(string sessionId, JsonElement updateElement, out CliMessage message)
    {
        message = null!;
        if (!string.Equals(TryGetString(updateElement, "name"), AntigravityUserInputRequest.QuestionToolName, StringComparison.Ordinal) ||
            TryGetString(updateElement, "toolCallId") is not { Length: > 0 } toolCallId ||
            !updateElement.TryGetProperty("rawInput", out var rawInput) ||
            rawInput.ValueKind != JsonValueKind.Object ||
            !rawInput.TryGetProperty("questions", out var questions) ||
            questions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var normalizedQuestions = questions.EnumerateArray()
            .Where(static question => question.ValueKind == JsonValueKind.Object)
            .Select(static question => new
            {
                question = TryGetString(question, "question") ?? string.Empty,
                header = TryGetString(question, "header") ?? string.Empty,
                multi_select = question.TryGetProperty("is_multi_select", out var multi) && multi.ValueKind == JsonValueKind.True,
                options = ReadQuestionOptions(question)
            })
            .Where(static question => question.question.Length > 0)
            .ToArray();

        if (normalizedQuestions.Length == 0)
        {
            return false;
        }

        message = new CliMessage(
            AntigravityUserInputRequest.MessageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = AntigravityUserInputRequest.MessageType,
                ["session_id"] = sessionId,
                ["tool_call_id"] = toolCallId,
                ["questions"] = normalizedQuestions
            }));
        return true;
    }

    private static object[] ReadQuestionOptions(JsonElement question)
    {
        if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return options.EnumerateArray()
            .Select(static option => option.ValueKind switch
            {
                JsonValueKind.String => new { label = option.GetString() ?? string.Empty, description = string.Empty },
                JsonValueKind.Object => new { label = TryGetString(option, "label") ?? string.Empty, description = TryGetString(option, "description") ?? string.Empty },
                _ => new { label = string.Empty, description = string.Empty }
            })
            .Where(static option => option.label.Length > 0)
            .Cast<object>()
            .ToArray();
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
            TryGetMeaningfulText(updateElement, out var directText, "text", "message");
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
        if (TryGetMeaningfulText(contentElement, out var directText, "text"))
        {
            return directText;
        }

        if (TryGetMeaningfulText(contentElement, out var directMessage, "message"))
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
            if (HasMeaningfulText(text))
            {
                parts.Add(text!);
            }
        }

        return parts.Count == 0 ? null : string.Concat(parts);
    }

    private static bool TryGetMeaningfulText(JsonElement element, out string? text, params string[] propertyNames)
    {
        text = null;
        foreach (var propertyName in propertyNames)
        {
            var candidate = TryGetString(element, propertyName);
            if (HasMeaningfulText(candidate))
            {
                text = candidate;
                return true;
            }
        }

        return false;
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

    private static bool HasMeaningfulText(string? text)
    {
        return !string.IsNullOrEmpty(text);
    }

    private static bool IsFailureStopReason(string? stopReason)
    {
        return string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "rejected", StringComparison.OrdinalIgnoreCase);
    }
}
