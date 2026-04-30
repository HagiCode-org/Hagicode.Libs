using ManagedCode.CodexSharpSDK.Models;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Shared helpers for interpreting terminal Codex SDK events.
/// </summary>
public static class CodexEventInspector
{
    /// <summary>
    /// Determines whether the event terminates the current execution stream.
    /// </summary>
    public static bool IsTerminalEvent(ThreadEvent threadEvent)
    {
        ArgumentNullException.ThrowIfNull(threadEvent);
        return threadEvent is TurnCompletedEvent or TurnFailedEvent or ThreadErrorEvent;
    }

    /// <summary>
    /// Extracts the terminal text associated with a terminal event.
    /// </summary>
    public static bool TryExtractTerminalMessage(ThreadEvent threadEvent, out string? terminalMessage)
    {
        ArgumentNullException.ThrowIfNull(threadEvent);

        terminalMessage = threadEvent switch
        {
            TurnCompletedEvent completedEvent when !string.IsNullOrWhiteSpace(completedEvent.Result) => completedEvent.Result,
            TurnFailedEvent failedEvent when !string.IsNullOrWhiteSpace(failedEvent.Error.Message) => failedEvent.Error.Message,
            ThreadErrorEvent errorEvent when !string.IsNullOrWhiteSpace(errorEvent.Message) => errorEvent.Message,
            _ => null
        };

        return !string.IsNullOrWhiteSpace(terminalMessage);
    }
}
