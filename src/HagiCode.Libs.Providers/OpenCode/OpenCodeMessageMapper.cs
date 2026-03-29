using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.OpenCode;

internal static class OpenCodeMessageMapper
{
    public static CliMessage CreateSessionLifecycleMessage(string sessionId, bool resumed, string? requestedSessionId = null)
    {
        var type = resumed ? "session.resumed" : "session.started";
        return new CliMessage(
            type,
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = type,
                ["session_id"] = sessionId,
                ["requested_session_id"] = requestedSessionId,
                ["resumed"] = resumed,
                ["restarted"] = !resumed && !string.IsNullOrWhiteSpace(requestedSessionId),
            }));
    }

    public static CliMessage CreateAssistantMessage(string sessionId, string text, string? messageId = null)
    {
        return new CliMessage(
            "assistant",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "assistant",
                ["session_id"] = sessionId,
                ["message_id"] = messageId,
                ["text"] = text,
            }));
    }

    public static CliMessage CreateTerminalCompletedMessage(string sessionId, string text, string? messageId = null)
    {
        return new CliMessage(
            "terminal.completed",
            JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "terminal.completed",
                ["session_id"] = sessionId,
                ["message_id"] = messageId,
                ["stop_reason"] = "end_turn",
                ["text"] = text,
            }));
    }
}
