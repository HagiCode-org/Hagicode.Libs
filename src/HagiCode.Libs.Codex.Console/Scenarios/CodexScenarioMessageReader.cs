using HagiCode.Libs.Providers.Codex;
using ManagedCode.CodexSharpSDK.Models;

namespace HagiCode.Libs.Codex.Console.Scenarios;

internal static class CodexScenarioMessageReader
{
    public static async Task<CodexScenarioExecutionResult> ReadExecutionResultAsync(
        ICodexProvider provider,
        CodexSessionOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        string? threadId = null;

        await using var session = await provider.CreateSessionAsync(options, cancellationToken);
        var streamedResult = await session.Thread.RunStreamedAsync(prompt);

        await foreach (var threadEvent in streamedResult.Events.WithCancellation(cancellationToken))
        {
            if (TryGetFailureMessage(threadEvent, out var failureMessage))
            {
                throw new InvalidOperationException(failureMessage);
            }

            if (threadEvent is ThreadStartedEvent startedEvent)
            {
                threadId ??= startedEvent.ThreadId;
            }

            if (TryExtractAssistantText(threadEvent, out var assistantText) &&
                !string.IsNullOrWhiteSpace(assistantText))
            {
                messages.Add(assistantText);
            }

            if (threadEvent is TurnCompletedEvent)
            {
                break;
            }
        }

        return new CodexScenarioExecutionResult(messages, threadId);
    }

    public static async Task<IReadOnlyList<string>> ReadAssistantMessagesAsync(
        ICodexProvider provider,
        CodexSessionOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var result = await ReadExecutionResultAsync(provider, options, prompt, cancellationToken);
        return result.Messages;
    }

    private static bool TryExtractAssistantText(ThreadEvent threadEvent, out string? text)
    {
        text = threadEvent switch
        {
            ItemCompletedEvent { Item: AgentMessageItem agentMessageItem } => agentMessageItem.Text,
            ItemUpdatedEvent { Item: AgentMessageItem agentMessageItem } => agentMessageItem.Text,
            _ => null
        };

        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryGetFailureMessage(ThreadEvent threadEvent, out string? message)
    {
        message = threadEvent switch
        {
            TurnFailedEvent failedEvent => failedEvent.Error.Message,
            ThreadErrorEvent errorEvent => errorEvent.Message,
            _ => null
        };

        return !string.IsNullOrWhiteSpace(message);
    }
}

internal sealed record CodexScenarioExecutionResult(
    IReadOnlyList<string> Messages,
    string? ThreadId);
