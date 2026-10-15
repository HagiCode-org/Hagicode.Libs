using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Reasonix;

internal static class ReasonixAcpMessageMapper
{
    private static readonly Regex UnknownModelPattern = new(
        "unknown model \"(?<model>[^\"]+)\"",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UnknownSessionPattern = new(
        @"unknown session (?<session>[^\s""]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

    /// <summary>
    /// Creates the terminal message from the prompt result. When the provider declined a question during the turn and the
    /// prompt ended with stop reason <c>error</c> or <c>cancelled</c> (how Reasonix ends a turn after a declined <c>ask</c>),
    /// the turn is a completion marked <c>question_declined</c> that keeps the original <c>stop_reason</c>.
    /// </summary>
    public static CliMessage CreateTerminalMessage(string sessionId, JsonElement promptResult, bool questionDeclined = false)
    {
        var stopReason = TryGetPromptResultStopReason(promptResult);
        var endedByDeclinedQuestion = questionDeclined && IsDeclinedQuestionStopReason(stopReason);
        var messageType = IsFailureStopReason(stopReason) && !endedByDeclinedQuestion ? "terminal.failed" : "terminal.completed";

        var content = new Dictionary<string, object?>
        {
            ["type"] = messageType,
            ["session_id"] = sessionId,
            ["stop_reason"] = stopReason,
            ["text"] = TryExtractPromptResultText(promptResult, out var text) ? text : null,
            ["result"] = promptResult
        };
        if (endedByDeclinedQuestion)
        {
            content["question_declined"] = true;
        }

        return new CliMessage(messageType, JsonSerializer.SerializeToElement(content));
    }

    /// <summary>
    /// Creates the <see cref="ReasonixUserInputRequest.MessageType" /> stream message for a question request. It carries the
    /// raw permission parameters, so <see cref="ReasonixUserInputRequest.TryFromCliMessage" /> reads the same question back.
    /// </summary>
    public static CliMessage CreateQuestionMessage(JsonElement parameters, ReasonixUserInputRequest request)
    {
        return new CliMessage(
            ReasonixUserInputRequest.MessageType,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = ReasonixUserInputRequest.MessageType,
                ["session_id"] = TryGetString(parameters, "sessionId"),
                ["request_id"] = request.RequestId,
                ["question"] = request.Question.Question,
                ["params"] = parameters
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

    /// <summary>
    /// Creates the terminal failure for a session that could not be started. Unknown model refs and unknown
    /// session ids carry a <c>failure_kind</c> so the caller can react without parsing free text.
    /// </summary>
    public static CliMessage CreateSessionStartFailureMessage(string? sessionId, string? model, Exception exception)
    {
        var detail = exception.Message;
        string? failureKind = null;
        string message;

        var unknownModel = UnknownModelPattern.Match(detail);
        var unknownSession = UnknownSessionPattern.Match(detail);
        if (unknownModel.Success)
        {
            failureKind = "unknown_model";
            var modelRef = unknownModel.Groups["model"].Value;
            message = $"Reasonix does not know the model \"{modelRef}\". Define it in your Reasonix configuration as a provider or provider/model ref, then try again.";
        }
        else if (unknownSession.Success)
        {
            failureKind = "unknown_session";
            var unknownSessionId = unknownSession.Groups["session"].Value;
            message = $"Reasonix no longer has session \"{unknownSessionId}\". Start a new session to continue.";
        }
        else
        {
            message = string.IsNullOrWhiteSpace(model)
                ? $"Reasonix ACP session failed to start: {detail}"
                : $"Reasonix ACP session failed to start for model \"{model}\": {detail}";
        }

        return new CliMessage(
            "terminal.failed",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "terminal.failed",
                ["session_id"] = sessionId,
                ["message"] = message,
                ["failure_kind"] = failureKind,
                ["detail"] = detail
            }));
    }

    /// <summary>
    /// Normalizes one ACP notification. With <paramref name="routeQuestions" /> set (a user-input handler is installed), a
    /// readable Reasonix question request becomes one <see cref="ReasonixUserInputRequest.MessageType" /> message.
    /// </summary>
    public static IReadOnlyList<CliMessage> NormalizeNotification(AcpNotification notification, bool routeQuestions = false)
    {
        if (routeQuestions && TryReadQuestionRequest(notification, out var questionRequest))
        {
            return [CreateQuestionMessage(notification.Parameters, questionRequest)];
        }

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
            // Replayed history only: a live turn never produces user_message_chunk.
            "user_message_chunk" => [],
            "tool_call" => [CreateUpdateMessage("tool.call", sessionId, AcpToolKindAnnotator.Annotate(updateElement))],
            "tool_call_update" => [CreateUpdateMessage("tool.update", sessionId, AcpToolKindAnnotator.Annotate(updateElement))],
            "prompt_completed" => [CreatePromptCompletedMessage(sessionId, updateElement)],
            "error" or "agent_error" or "prompt_failed" => [CreatePromptFailureMessage(sessionId, updateElement)],
            _ =>
            [
                CreateUpdateMessage("session.update", sessionId, updateElement)
            ]
        };
    }

    /// <summary>
    /// Reads a Reasonix question from a <c>session/request_permission</c> notification.
    /// </summary>
    /// <returns><see langword="false" /> for any other notification, a tool approval, or an unreadable question.</returns>
    public static bool TryReadQuestionRequest(AcpNotification notification, out ReasonixUserInputRequest request)
    {
        request = null!;
        return IsPermissionRequestNotification(notification) &&
               ReasonixUserInputRequest.TryParse(AcpPermissionRequest.FromParameters(notification.Parameters), out request);
    }

    /// <summary>
    /// Returns <see langword="true" /> when the notification is a Reasonix question request, readable or not, and gives the
    /// tool call id and question text that name it.
    /// </summary>
    public static bool IsQuestionRequestNotification(AcpNotification notification, out string? questionId, out string? questionText)
    {
        questionId = null;
        questionText = null;
        if (!IsPermissionRequestNotification(notification))
        {
            return false;
        }

        var permission = AcpPermissionRequest.FromParameters(notification.Parameters);
        if (!ReasonixUserInputRequest.IsQuestionRequest(permission))
        {
            return false;
        }

        questionId = permission.ToolCallId;
        questionText = ReasonixUserInputRequest.TryParse(permission, out var request) ? request.Question.Question : null;
        return true;
    }

    private static bool IsPermissionRequestNotification(AcpNotification notification)
    {
        return string.Equals(notification.Method, ReasonixUserInputRequest.PermissionMethod, StringComparison.OrdinalIgnoreCase) &&
               notification.Parameters.ValueKind == JsonValueKind.Object;
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

    public static bool IsReplayAssistantNotification(AcpNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!notification.Parameters.TryGetProperty("update", out var updateElement) ||
            updateElement.ValueKind != JsonValueKind.Object ||
            !string.Equals(TryGetString(updateElement, "sessionUpdate"), "agent_message_chunk", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!notification.Parameters.TryGetProperty("_meta", out var metaElement) ||
            metaElement.ValueKind != JsonValueKind.Object)
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

    private static CliMessage CreatePromptFailureMessage(string sessionId, JsonElement updateElement)
    {
        return new CliMessage(
            "terminal.failed",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "terminal.failed",
                ["session_id"] = sessionId,
                ["message"] = TryGetString(updateElement, "message") ?? TryGetString(updateElement, "reason") ?? ExtractText(updateElement),
                ["update"] = updateElement
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

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }

        return propertyElement.GetBoolean();
    }

    private static bool IsDeclinedQuestionStopReason(string? stopReason)
    {
        return string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFailureStopReason(string? stopReason)
    {
        return string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "cancelled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "canceled", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(stopReason, "rejected", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Drops replayed history on the <c>session/load</c> path using message shape rather than <c>_meta</c>. A
/// <c>tool_call</c> that first appears already completed or failed is replay (live calls start pending), so it
/// and every <c>tool_call_update</c> sharing its id are dropped.
/// </summary>
internal sealed class ReasonixReplayShapeFilter
{
    private readonly HashSet<string> _seenToolCallIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _replayedToolCallIds = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> when the notification is replayed history that must not reach the turn output.
    /// </summary>
    public bool ShouldDrop(AcpNotification notification)
    {
        if (!string.Equals(notification.Method, "session/update", StringComparison.OrdinalIgnoreCase) ||
            notification.Parameters.ValueKind != JsonValueKind.Object ||
            !notification.Parameters.TryGetProperty("update", out var updateElement) ||
            updateElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var toolCallId = TryGetString(updateElement, "toolCallId");
        if (toolCallId is null)
        {
            return false;
        }

        switch (TryGetString(updateElement, "sessionUpdate"))
        {
            case "tool_call":
                if (!_seenToolCallIds.Add(toolCallId))
                {
                    return false;
                }

                if (!IsTerminalToolStatus(TryGetString(updateElement, "status")))
                {
                    return false;
                }

                _replayedToolCallIds.Add(toolCallId);
                return true;
            case "tool_call_update":
                return _replayedToolCallIds.Contains(toolCallId);
            default:
                return false;
        }
    }

    private static bool IsTerminalToolStatus(string? status)
    {
        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var propertyElement) &&
               propertyElement.ValueKind == JsonValueKind.String
            ? propertyElement.GetString()
            : null;
    }
}
