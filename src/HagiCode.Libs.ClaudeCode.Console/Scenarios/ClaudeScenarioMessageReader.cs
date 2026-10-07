using System.Text.Json;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console.Scenarios;

internal static class ClaudeScenarioMessageReader
{
    public static async Task<(IReadOnlyList<string> Messages, int AssistantMessageCount)> ReadAssistantMessagesAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeCodeOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var (messages, assistantMessageCount, _) = await ReadAssistantMessagesWithSessionIdAsync(
            provider,
            options,
            prompt,
            cancellationToken);
        return (messages, assistantMessageCount);
    }

    /// <summary>
    /// Reads assistant text like <see cref="ReadAssistantMessagesAsync" /> and also reports the
    /// <c>session_id</c> the CLI stamped on the stream, so a later request can <c>--resume</c> it.
    /// </summary>
    public static async Task<(IReadOnlyList<string> Messages, int AssistantMessageCount, string? SessionId)> ReadAssistantMessagesWithSessionIdAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeCodeOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var assistantMessageCount = 0;
        string? sessionId = null;

        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            sessionId ??= ExtractSessionId(message.Content);

            if (!string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            assistantMessageCount++;
            var content = ExtractTextContent(message.Content);
            if (!string.IsNullOrWhiteSpace(content))
            {
                messages.Add(content);
            }
        }

        return (messages, assistantMessageCount, sessionId);
    }

    private static string? ExtractSessionId(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("session_id", out var sessionIdElement) &&
            sessionIdElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
        {
            return sessionIdElement.GetString();
        }

        return null;
    }

    private static string? ExtractTextContent(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("message", out var messageElement) &&
                TryExtractMessageText(messageElement, out var messageText))
            {
                return messageText;
            }

            foreach (var property in content.EnumerateObject())
            {
                if (property.NameEquals("content") && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }

        return null;
    }

    private static bool TryExtractMessageText(JsonElement messageElement, out string? text)
    {
        text = null;
        if (messageElement.ValueKind != JsonValueKind.Object ||
            !messageElement.TryGetProperty("content", out var contentElement))
        {
            return false;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            text = contentElement.GetString();
            return !string.IsNullOrWhiteSpace(text);
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var parts = new List<string>();
        foreach (var item in contentElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("type", out var typeElement) &&
                typeElement.ValueKind == JsonValueKind.String &&
                string.Equals(typeElement.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("text", out var textElement) &&
                textElement.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(textElement.GetString()))
            {
                parts.Add(textElement.GetString()!);
            }
        }

        if (parts.Count == 0)
        {
            return false;
        }

        text = string.Join(" ", parts);
        return true;
    }
}
