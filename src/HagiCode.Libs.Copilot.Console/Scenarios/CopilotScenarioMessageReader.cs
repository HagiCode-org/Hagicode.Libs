using System.Text.Json;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Copilot;

namespace HagiCode.Libs.Copilot.Console.Scenarios;

internal static class CopilotScenarioMessageReader
{
    public static async Task<CopilotScenarioExecutionResult> ReadExecutionResultAsync(
        ICliProvider<CopilotOptions> provider,
        CopilotOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var toolEvents = new List<string>();

        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            if (TryGetFailureMessage(message.Content, out var failureMessage))
            {
                throw new InvalidOperationException(failureMessage);
            }

            if (TryExtractAssistantText(message.Content, out var assistantText) &&
                !string.IsNullOrWhiteSpace(assistantText))
            {
                messages.Add(assistantText);
            }

            if (TryExtractToolEvent(message.Content, out var toolEvent))
            {
                toolEvents.Add(toolEvent!);
            }

            if (string.Equals(message.Type, "result", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return new CopilotScenarioExecutionResult(messages, toolEvents);
    }

    public static async Task<IReadOnlyList<string>> ReadAssistantMessagesAsync(
        ICliProvider<CopilotOptions> provider,
        CopilotOptions options,
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

        if (!content.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(typeElement.GetString(), "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!content.TryGetProperty("text", out var textElement) || textElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = textElement.GetString();
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryExtractToolEvent(JsonElement content, out string? toolEvent)
    {
        toolEvent = null;
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!content.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var type = typeElement.GetString();
        if (!string.Equals(type, "tool.started", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "tool.completed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        toolEvent = type;
        return true;
    }

    private static bool TryGetFailureMessage(JsonElement content, out string? message)
    {
        message = null;
        if (content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!content.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!string.Equals(typeElement.GetString(), "error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!content.TryGetProperty("message", out var messageElement) || messageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        message = messageElement.GetString();
        return !string.IsNullOrWhiteSpace(message);
    }
}

internal sealed record CopilotScenarioExecutionResult(
    IReadOnlyList<string> Messages,
    IReadOnlyList<string> ToolEvents);
