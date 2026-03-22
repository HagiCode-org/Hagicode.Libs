using System.Text.Json;
using System.Text;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Kiro;

namespace HagiCode.Libs.Kiro.Console.Scenarios;

internal static class KiroScenarioMessageReader
{
    public static async Task<KiroScenarioExecutionResult> ReadExecutionResultAsync(
        ICliProvider<KiroOptions> provider,
        KiroOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var assistantTextBuilder = new StringBuilder();
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

            if (string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase) &&
                TryGetText(message.Content, out var assistantText) &&
                !string.IsNullOrWhiteSpace(assistantText))
            {
                messages.Add(assistantText);
                assistantTextBuilder.Append(assistantText);
            }

            if (string.Equals(message.Type, "terminal.completed", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return new KiroScenarioExecutionResult(messages, assistantTextBuilder.ToString().Trim(), sessionId);
    }

    public static async Task<IReadOnlyList<string>> ReadAssistantMessagesAsync(
        ICliProvider<KiroOptions> provider,
        KiroOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadExecutionResultAsync(provider, options, prompt, cancellationToken);
        return result.Messages;
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

internal sealed record KiroScenarioExecutionResult(
    IReadOnlyList<string> Messages,
    string AssistantText,
    string? SessionId);
