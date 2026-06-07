using System.Text;
using System.Text.Json;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Pi.Console.Scenarios;

internal static class PiScenarioMessageReader
{
    public static async Task<PiScenarioExecutionResult> ReadExecutionResultAsync(
        ICliProvider<PiOptions> provider,
        PiOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        var assistantTextBuilder = new StringBuilder();
        var accumulator = new AssistantSnapshotAccumulator();
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
                var delta = accumulator.ReconcileSnapshot(assistantText);
                if (!string.IsNullOrEmpty(delta))
                {
                    messages.Add(delta);
                    assistantTextBuilder.Append(delta);
                }
            }

            if (string.Equals(message.Type, "terminal.completed", StringComparison.OrdinalIgnoreCase))
            {
                if (TryGetText(message.Content, out var terminalText) &&
                    !string.IsNullOrWhiteSpace(terminalText))
                {
                    var completionDelta = accumulator.ReconcileCompletion(terminalText);
                    if (!string.IsNullOrEmpty(completionDelta))
                    {
                        messages.Add(completionDelta);
                        assistantTextBuilder.Append(completionDelta);
                    }
                }

                break;
            }
        }

        return new PiScenarioExecutionResult(messages, assistantTextBuilder.ToString().Trim(), sessionId);
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

    private sealed class AssistantSnapshotAccumulator
    {
        private string? _currentSnapshot;

        public string ReconcileSnapshot(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            if (_currentSnapshot is null)
            {
                _currentSnapshot = text;
                return text;
            }

            if (text.StartsWith(_currentSnapshot, StringComparison.Ordinal))
            {
                var delta = text[_currentSnapshot.Length..];
                _currentSnapshot = text;
                return delta;
            }

            if (_currentSnapshot.StartsWith(text, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            _currentSnapshot += text;
            return text;
        }

        public string ReconcileCompletion(string text)
        {
            ArgumentNullException.ThrowIfNull(text);

            if (_currentSnapshot is null)
            {
                _currentSnapshot = text;
                return text;
            }

            if (text.StartsWith(_currentSnapshot, StringComparison.Ordinal))
            {
                var delta = text[_currentSnapshot.Length..];
                _currentSnapshot = text;
                return delta;
            }

            if (_currentSnapshot.StartsWith(text, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            _currentSnapshot = text;
            return text;
        }
    }
}

internal sealed record PiScenarioExecutionResult(
    IReadOnlyList<string> Messages,
    string AssistantText,
    string? SessionId);
