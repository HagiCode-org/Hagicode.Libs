using System.Text.Json;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Codex.Console.Scenarios;

internal static class CodexScenarioMessageReader
{
    public static async Task<CodexScenarioExecutionResult> ReadExecutionResultAsync(
        ICliProvider<CodexOptions> provider,
        CodexOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        string? threadId = null;

        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            if (TryGetFailureMessage(message.Content, out var failureMessage))
            {
                throw new InvalidOperationException(failureMessage);
            }

            if (TryGetThreadId(message.Content, out var resolvedThreadId))
            {
                threadId ??= resolvedThreadId;
            }

            if (TryExtractAssistantText(message.Content, out var assistantText) &&
                !string.IsNullOrWhiteSpace(assistantText))
            {
                messages.Add(assistantText);
            }

            if (string.Equals(message.Type, "turn.completed", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return new CodexScenarioExecutionResult(messages, threadId);
    }

    public static async Task<IReadOnlyList<string>> ReadAssistantMessagesAsync(
        ICliProvider<CodexOptions> provider,
        CodexOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadExecutionResultAsync(provider, options, prompt, cancellationToken);
        return result.Messages;
    }

    private static bool TryExtractAssistantText(JsonElement content, out string? text)
    {
        text = null;

        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryGetAgentMessageText(content, out text))
        {
            return true;
        }

        if (!content.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return TryGetAgentMessageText(itemElement, out text);
    }

    private static bool TryGetAgentMessageText(JsonElement element, out string? text)
    {
        text = null;

        if (!element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(typeElement.GetString(), "agent_message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!element.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
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
            string.Equals(typeElement.GetString(), "turn.failed", StringComparison.OrdinalIgnoreCase) &&
            content.TryGetProperty("error", out var errorElement) &&
            errorElement.ValueKind == JsonValueKind.Object &&
            errorElement.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString();
            return !string.IsNullOrWhiteSpace(message);
        }

        if (content.TryGetProperty("type", out typeElement) &&
            typeElement.ValueKind == JsonValueKind.String &&
            string.Equals(typeElement.GetString(), "error", StringComparison.OrdinalIgnoreCase) &&
            content.TryGetProperty("message", out var directMessageElement) &&
            directMessageElement.ValueKind == JsonValueKind.String)
        {
            message = directMessageElement.GetString();
            return !string.IsNullOrWhiteSpace(message);
        }

        return false;
    }

    private static bool TryGetThreadId(JsonElement content, out string? threadId)
    {
        threadId = null;
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!content.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(typeElement.GetString(), "thread.started", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!content.TryGetProperty("thread_id", out var threadIdElement) ||
            threadIdElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        threadId = threadIdElement.GetString();
        return !string.IsNullOrWhiteSpace(threadId);
    }
}

internal sealed record CodexScenarioExecutionResult(
    IReadOnlyList<string> Messages,
    string? ThreadId);
