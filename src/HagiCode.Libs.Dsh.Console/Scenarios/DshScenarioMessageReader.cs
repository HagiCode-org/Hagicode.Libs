using System.Text;
using System.Text.Json;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Dsh;

namespace HagiCode.Libs.Dsh.Console.Scenarios;

internal static class DshScenarioMessageReader
{
    public static async Task<DshScenarioExecutionResult> ReadExecutionResultAsync(
        ICliProvider<DshOptions> provider,
        DshOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var assistantTextBuilder = new StringBuilder();
        var toolCalls = new List<DshScenarioToolCall>();
        var assistantMessageIds = new List<string>();
        string? sessionId = null;

        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            if (TryGetFailureMessage(message.Content, out var failureMessage))
            {
                throw new InvalidOperationException(failureMessage);
            }

            if (TryGetSessionId(message.Content, out var resolvedSessionId))
            {
                sessionId ??= resolvedSessionId;
            }

            if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetUpdateString(message.Content, "messageId", out var messageId) &&
                    !assistantMessageIds.Contains(messageId!, StringComparer.Ordinal))
                {
                    assistantMessageIds.Add(messageId!);
                }

                if (TryGetText(message.Content, out var assistantText) &&
                    !string.IsNullOrWhiteSpace(assistantText))
                {
                    messages.Add(assistantText);
                    assistantTextBuilder.Append(assistantText);
                }
            }

            if (string.Equals(message.Type, "tool.call", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message.Type, "tool.update", StringComparison.OrdinalIgnoreCase))
            {
                RecordToolMessage(toolCalls, message.Type, message.Content);
            }

            if (string.Equals(message.Type, "terminal.completed", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return new DshScenarioExecutionResult(
            messages,
            assistantTextBuilder.ToString().Trim(),
            sessionId,
            toolCalls,
            assistantMessageIds.Count);
    }

    private static void RecordToolMessage(List<DshScenarioToolCall> toolCalls, string messageType, JsonElement content)
    {
        if (!TryGetUpdateString(content, "toolCallId", out var toolCallId))
        {
            return;
        }

        TryGetUpdateString(content, "status", out var status);
        var name = ResolveToolName(content);
        var index = toolCalls.FindIndex(call => string.Equals(call.Id, toolCallId, StringComparison.Ordinal));
        if (index < 0)
        {
            toolCalls.Add(new DshScenarioToolCall(
                toolCallId!,
                name ?? "tool_call",
                status ?? (string.Equals(messageType, "tool.call", StringComparison.OrdinalIgnoreCase) ? "in_progress" : "unknown")));
            return;
        }

        var existing = toolCalls[index];
        toolCalls[index] = existing with
        {
            Name = name ?? existing.Name,
            Status = status ?? existing.Status,
        };
    }

    private static string? ResolveToolName(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("update", out var update) &&
            update.ValueKind == JsonValueKind.Object &&
            update.TryGetProperty("rawInput", out var rawInput) &&
            rawInput.ValueKind == JsonValueKind.Object &&
            rawInput.TryGetProperty("description", out var description) &&
            description.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(description.GetString()))
        {
            return description.GetString();
        }

        if (TryGetUpdateString(content, "title", out var title))
        {
            return title;
        }

        return TryGetUpdateString(content, "kind", out var kind) ? kind : null;
    }

    private static bool TryGetUpdateString(JsonElement content, string propertyName, out string? value)
    {
        value = null;
        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("update", out var update) ||
            update.ValueKind != JsonValueKind.Object ||
            !update.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetSessionId(JsonElement content, out string? sessionId)
    {
        sessionId = null;
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!content.TryGetProperty("session_id", out var sessionIdElement) ||
            sessionIdElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        sessionId = sessionIdElement.GetString();
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private static bool TryGetText(JsonElement content, out string? text)
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

    private static bool TryGetFailureMessage(JsonElement content, out string? message)
    {
        message = null;
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (content.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "terminal.failed", StringComparison.OrdinalIgnoreCase))
        {
            if (content.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
                return !string.IsNullOrWhiteSpace(message);
            }
        }

        return false;
    }
}

internal sealed record DshScenarioExecutionResult(
    IReadOnlyList<string> Messages,
    string AssistantText,
    string? SessionId,
    IReadOnlyList<DshScenarioToolCall> ToolCalls,
    int AssistantMessageIdCount);

internal sealed record DshScenarioToolCall(string Id, string Name, string Status);
