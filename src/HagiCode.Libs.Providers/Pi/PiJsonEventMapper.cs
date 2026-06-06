using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Pi;

internal sealed class PiJsonEventMapper
{
    public IReadOnlyList<CliMessage> Normalize(ProcessResult result, string? requestedSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var events = ParseJsonEvents(result.StandardOutput, out var invalidOutputLines);
        var messages = new List<CliMessage>();

        string? sessionId = null;
        string? assistantText = null;
        string? assistantModel = null;
        string? assistantProvider = null;
        string? stopReason = null;
        string? errorText = null;
        CliMessage? terminalMessage = null;

        foreach (var eventElement in events)
        {
            var eventType = GetString(eventElement, "type");
            switch (eventType)
            {
                case "session":
                    sessionId ??= GetString(eventElement, "id");
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        messages.Add(CreateSessionLifecycleMessage(sessionId!, eventElement, requestedSessionId));
                    }

                    break;
                case "message_end":
                    if (TryGetAssistantMessage(eventElement, out var assistantMessage))
                    {
                        CaptureAssistantState(
                            assistantMessage,
                            ref assistantText,
                            ref assistantModel,
                            ref assistantProvider,
                            ref stopReason,
                            ref errorText);

                        if (!string.IsNullOrWhiteSpace(assistantText))
                        {
                            messages.Add(CreateAssistantMessage(sessionId, assistantText!, assistantMessage));
                        }
                    }

                    break;
                case "turn_end":
                    if (TryGetAssistantMessage(eventElement, out var turnMessage))
                    {
                        CaptureAssistantState(
                            turnMessage,
                            ref assistantText,
                            ref assistantModel,
                            ref assistantProvider,
                            ref stopReason,
                            ref errorText);
                        terminalMessage = CreateTerminalMessage(sessionId, assistantText, turnMessage, sourceEventType: eventType);
                    }

                    break;
                case "agent_end":
                    if (terminalMessage is null && TryGetLastAssistantMessage(eventElement, out var finalAssistantMessage))
                    {
                        CaptureAssistantState(
                            finalAssistantMessage,
                            ref assistantText,
                            ref assistantModel,
                            ref assistantProvider,
                            ref stopReason,
                            ref errorText);
                        terminalMessage = CreateTerminalMessage(sessionId, assistantText, finalAssistantMessage, sourceEventType: eventType);
                    }

                    break;
            }
        }

        if (result.ExitCode != 0)
        {
            terminalMessage = CreateTerminalFailedMessage(
                sessionId,
                BuildProcessFailureText(result, invalidOutputLines, errorText),
                assistantModel,
                assistantProvider,
                stopReason ?? "exit_code",
                result.ExitCode,
                result.StandardError,
                invalidOutputLines);
        }
        else if (terminalMessage is null)
        {
            terminalMessage = CreateFallbackTerminalMessage(
                result,
                invalidOutputLines,
                sessionId,
                assistantText,
                assistantModel,
                assistantProvider,
                stopReason,
                errorText);
        }

        if (terminalMessage is not null)
        {
            messages.Add(terminalMessage);
        }

        return messages;
    }

    private static IReadOnlyList<JsonElement> ParseJsonEvents(string stdout, out IReadOnlyList<string> invalidOutputLines)
    {
        var events = new List<JsonElement>();
        var invalidLines = new List<string>();

        using var reader = new StringReader(stdout ?? string.Empty);
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var rootElement = document.RootElement;
                if (rootElement.ValueKind != JsonValueKind.Object || !rootElement.TryGetProperty("type", out _))
                {
                    invalidLines.Add(line);
                    continue;
                }

                events.Add(JsonSerializer.SerializeToElement(rootElement));
            }
            catch (JsonException)
            {
                invalidLines.Add(line);
            }
        }

        invalidOutputLines = invalidLines;
        return events;
    }

    private static void CaptureAssistantState(
        JsonElement assistantMessage,
        ref string? assistantText,
        ref string? assistantModel,
        ref string? assistantProvider,
        ref string? stopReason,
        ref string? errorText)
    {
        assistantText = ExtractAssistantText(assistantMessage) ?? assistantText;
        assistantModel = GetString(assistantMessage, "responseModel") ?? GetString(assistantMessage, "model") ?? assistantModel;
        assistantProvider = GetString(assistantMessage, "provider") ?? assistantProvider;
        stopReason = GetString(assistantMessage, "stopReason") ?? stopReason;
        errorText = GetString(assistantMessage, "errorMessage") ?? errorText;
    }

    private static CliMessage? CreateTerminalMessage(
        string? sessionId,
        string? assistantText,
        JsonElement assistantMessage,
        string sourceEventType)
    {
        var stopReason = GetString(assistantMessage, "stopReason");
        var provider = GetString(assistantMessage, "provider");
        var model = GetString(assistantMessage, "responseModel") ?? GetString(assistantMessage, "model");
        if (string.Equals(stopReason, "error", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stopReason, "aborted", StringComparison.OrdinalIgnoreCase))
        {
            var errorText = GetString(assistantMessage, "errorMessage")
                            ?? assistantText
                            ?? "Pi reported a failed assistant turn.";

            return CreateTerminalFailedMessage(sessionId, errorText, model, provider, stopReason, exitCode: null, stderr: null, invalidOutputLines: null, sourceEventType);
        }

        return CreateTerminalCompletedMessage(sessionId, assistantText, model, provider, stopReason, sourceEventType);
    }

    private static CliMessage CreateFallbackTerminalMessage(
        ProcessResult result,
        IReadOnlyList<string> invalidOutputLines,
        string? sessionId,
        string? assistantText,
        string? assistantModel,
        string? assistantProvider,
        string? stopReason,
        string? errorText)
    {
        if (!string.IsNullOrWhiteSpace(errorText))
        {
            return CreateTerminalFailedMessage(sessionId, errorText!, assistantModel, assistantProvider, stopReason ?? "error", result.ExitCode, result.StandardError, invalidOutputLines);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            return CreateTerminalFailedMessage(sessionId, result.StandardError.Trim(), assistantModel, assistantProvider, stopReason ?? "stderr", result.ExitCode, result.StandardError, invalidOutputLines);
        }

        if (invalidOutputLines.Count > 0)
        {
            return CreateTerminalFailedMessage(
                sessionId,
                $"Pi returned non-JSON output:{Environment.NewLine}{string.Join(Environment.NewLine, invalidOutputLines)}",
                assistantModel,
                assistantProvider,
                stopReason ?? "invalid_json",
                result.ExitCode,
                result.StandardError,
                invalidOutputLines);
        }

        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            return CreateTerminalCompletedMessage(sessionId, assistantText, assistantModel, assistantProvider, stopReason, sourceEventType: "fallback");
        }

        return CreateTerminalFailedMessage(
            sessionId,
            "Pi JSON output ended without a completion or failure event.",
            assistantModel,
            assistantProvider,
            stopReason ?? "missing_terminal_event",
            result.ExitCode,
            result.StandardError,
            invalidOutputLines);
    }

    private static CliMessage CreateSessionLifecycleMessage(string sessionId, JsonElement sessionEvent, string? requestedSessionId)
    {
        var requested = string.IsNullOrWhiteSpace(requestedSessionId) ? null : requestedSessionId.Trim();
        var isResumed = requested is not null && string.Equals(sessionId, requested, StringComparison.Ordinal);
        var isRestarted = requested is not null && !isResumed;
        var messageType = isResumed ? "session.resumed" : "session.started";

        var payload = new Dictionary<string, object?>
        {
            ["type"] = messageType,
            ["session_id"] = sessionId,
            ["sessionId"] = sessionId,
            ["resumeMode"] = isResumed ? "resumed" : isRestarted ? "restarted" : "started",
        };

        AddIfNotEmpty(payload, "requested_session_id", requested);
        AddIfNotEmpty(payload, "requestedSessionId", requested);
        AddIfNotEmpty(payload, "cwd", GetString(sessionEvent, "cwd"));
        AddIfNotEmpty(payload, "timestamp", GetString(sessionEvent, "timestamp"));
        AddIfPresent(payload, "version", TryGetInt32(sessionEvent, "version"));

        if (requested is not null)
        {
            payload["resumed"] = isResumed;
            payload["restarted"] = isRestarted;
        }

        return new CliMessage(messageType, JsonSerializer.SerializeToElement(payload));
    }

    private static CliMessage CreateAssistantMessage(string? sessionId, string text, JsonElement assistantMessage)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["text"] = text,
        };

        AddSessionId(payload, sessionId);
        AddIfNotEmpty(payload, "provider", GetString(assistantMessage, "provider"));
        AddIfNotEmpty(payload, "model", GetString(assistantMessage, "model"));
        AddIfNotEmpty(payload, "response_model", GetString(assistantMessage, "responseModel"));
        AddIfNotEmpty(payload, "response_id", GetString(assistantMessage, "responseId"));
        AddIfNotEmpty(payload, "stop_reason", GetString(assistantMessage, "stopReason"));

        return new CliMessage("assistant", JsonSerializer.SerializeToElement(payload));
    }

    private static CliMessage CreateTerminalCompletedMessage(
        string? sessionId,
        string? text,
        string? model,
        string? provider,
        string? stopReason,
        string? sourceEventType)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.completed",
        };

        AddSessionId(payload, sessionId);
        AddIfNotEmpty(payload, "text", text);
        AddIfNotEmpty(payload, "model", model);
        AddIfNotEmpty(payload, "provider", provider);
        AddIfNotEmpty(payload, "stop_reason", stopReason);
        AddIfNotEmpty(payload, "source_event_type", sourceEventType);

        return new CliMessage("terminal.completed", JsonSerializer.SerializeToElement(payload));
    }

    private static CliMessage CreateTerminalFailedMessage(
        string? sessionId,
        string diagnosticText,
        string? model,
        string? provider,
        string? stopReason,
        int? exitCode,
        string? stderr,
        IReadOnlyList<string>? invalidOutputLines,
        string? sourceEventType = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "terminal.failed",
            ["text"] = diagnosticText,
            ["error"] = diagnosticText,
            ["message"] = diagnosticText,
        };

        AddSessionId(payload, sessionId);
        AddIfNotEmpty(payload, "model", model);
        AddIfNotEmpty(payload, "provider", provider);
        AddIfNotEmpty(payload, "stop_reason", stopReason);
        AddIfNotEmpty(payload, "stderr", string.IsNullOrWhiteSpace(stderr) ? null : stderr.Trim());
        AddIfNotEmpty(payload, "source_event_type", sourceEventType);
        AddIfPresent(payload, "exit_code", exitCode);

        if (invalidOutputLines is { Count: > 0 })
        {
            payload["invalid_output_lines"] = invalidOutputLines.ToArray();
        }

        return new CliMessage("terminal.failed", JsonSerializer.SerializeToElement(payload));
    }

    private static string BuildProcessFailureText(ProcessResult result, IReadOnlyList<string> invalidOutputLines, string? errorText)
    {
        var builder = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(errorText))
        {
            builder.Append(errorText.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            AppendDiagnosticLine(builder, result.StandardError.Trim());
        }

        if (invalidOutputLines.Count > 0)
        {
            AppendDiagnosticLine(builder, string.Join(Environment.NewLine, invalidOutputLines));
        }

        if (builder.Length == 0)
        {
            builder.Append($"Pi exited with code {result.ExitCode}.");
        }
        else
        {
            AppendDiagnosticLine(builder, $"Pi exited with code {result.ExitCode}.");
        }

        return builder.ToString();
    }

    private static void AppendDiagnosticLine(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.Append(value);
    }

    private static string? ExtractAssistantText(JsonElement assistantMessage)
    {
        if (!assistantMessage.TryGetProperty("content", out var contentElement) || contentElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var contentItem in contentElement.EnumerateArray())
        {
            if (!string.Equals(GetString(contentItem, "type"), "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var text = GetString(contentItem, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.Append(text);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static bool TryGetAssistantMessage(JsonElement eventElement, out JsonElement assistantMessage)
    {
        if (eventElement.TryGetProperty("message", out assistantMessage)
            && assistantMessage.ValueKind == JsonValueKind.Object
            && string.Equals(GetString(assistantMessage, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        assistantMessage = default;
        return false;
    }

    private static bool TryGetLastAssistantMessage(JsonElement eventElement, out JsonElement assistantMessage)
    {
        if (eventElement.TryGetProperty("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
        {
            var assistantMessages = messagesElement
                .EnumerateArray()
                .Where(static message => string.Equals(GetString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (assistantMessages.Length > 0)
            {
                assistantMessage = assistantMessages[^1];
                return true;
            }
        }

        assistantMessage = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var valueElement)
            && valueElement.ValueKind == JsonValueKind.String)
        {
            return valueElement.GetString();
        }

        return null;
    }

    private static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out var valueElement)
            && valueElement.ValueKind == JsonValueKind.Number
            && valueElement.TryGetInt32(out var value))
        {
            return value;
        }

        return null;
    }

    private static void AddSessionId(Dictionary<string, object?> payload, string? sessionId)
    {
        AddIfNotEmpty(payload, "session_id", sessionId);
        AddIfNotEmpty(payload, "sessionId", sessionId);
    }

    private static void AddIfNotEmpty(Dictionary<string, object?> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value;
        }
    }

    private static void AddIfPresent<T>(Dictionary<string, object?> payload, string key, T? value)
        where T : struct
    {
        if (value.HasValue)
        {
            payload[key] = value.Value;
        }
    }
}
