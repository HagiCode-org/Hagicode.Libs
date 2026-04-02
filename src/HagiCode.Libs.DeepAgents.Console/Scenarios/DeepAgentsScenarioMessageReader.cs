using System.Text;
using System.Text.Json;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

internal static class DeepAgentsScenarioMessageReader
{
    private const int PreviewMaxLength = 240;

    public static async Task<DeepAgentsScenarioExecutionResult> ReadExecutionResultAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var messageTypes = new List<string>();
        var assistantTextBuilder = new StringBuilder();
        string? sessionId = null;

        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            messageTypes.Add(message.Type);

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

        return new DeepAgentsScenarioExecutionResult(messages, assistantTextBuilder.ToString().Trim(), sessionId, messageTypes);
    }

    public static IReadOnlyList<string>? BuildDetailLines(
        DeepAgentsConsoleExecutionOptions executionOptions,
        DeepAgentsOptions options,
        string prompt,
        DeepAgentsScenarioExecutionResult result,
        string? prefix = null)
    {
        if (!executionOptions.Verbose)
        {
            return null;
        }

        var labelPrefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : $"{prefix} ";
        return
        [
            $"{labelPrefix}Prompt: {BuildPreview(prompt)}",
            $"{labelPrefix}Model: {options.Model ?? "(default)"}",
            $"{labelPrefix}Mode: {options.ModeId ?? "(default)"}",
            $"{labelPrefix}Workspace: {options.WorkspaceRoot ?? options.WorkingDirectory ?? "(default)"}",
            $"{labelPrefix}SessionId: {result.SessionId ?? "(none)"}",
            $"{labelPrefix}MessageTypes: {(result.MessageTypes.Count == 0 ? "(none)" : string.Join(" -> ", result.MessageTypes))}",
            $"{labelPrefix}AssistantChars: {result.AssistantText.Length}",
            $"{labelPrefix}AssistantPreview: {BuildPreview(result.AssistantText)}"
        ];
    }

    public static IReadOnlyList<string>? CombineDetailLines(params IReadOnlyList<string>?[] blocks)
    {
        var combined = new List<string>();
        foreach (var block in blocks)
        {
            if (block is null)
            {
                continue;
            }

            combined.AddRange(block);
        }

        return combined.Count == 0 ? null : combined;
    }

    private static bool TryGetSessionId(JsonElement content, out string? sessionId)
    {
        sessionId = null;
        if (content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("session_id", out var sessionIdElement) ||
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
            string.Equals(typeElement.GetString(), "terminal.failed", StringComparison.OrdinalIgnoreCase) &&
            content.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.String)
        {
            message = messageElement.GetString();
            return !string.IsNullOrWhiteSpace(message);
        }

        return false;
    }

    private static string BuildPreview(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(empty)";
        }

        var normalized = value
            .Trim()
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return normalized.Length <= PreviewMaxLength
            ? normalized
            : $"{normalized[..PreviewMaxLength]}...";
    }
}

internal sealed record DeepAgentsScenarioExecutionResult(
    IReadOnlyList<string> Messages,
    string AssistantText,
    string? SessionId,
    IReadOnlyList<string> MessageTypes);
