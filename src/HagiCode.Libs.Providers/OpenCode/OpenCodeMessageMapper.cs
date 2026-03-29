using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.OpenCode;

internal static class OpenCodeMessageMapper
{
    public static CliMessage CreateSessionLifecycleMessage(
        string sessionId,
        string resumeMode,
        string? requestedSessionId = null,
        string? runtimeFingerprint = null,
        string? poolFingerprint = null)
    {
        var type = string.Equals(resumeMode, "resumed", StringComparison.OrdinalIgnoreCase)
            ? "session.resumed"
            : "session.started";
        return new CliMessage(
            type,
            JsonSerializer.SerializeToElement(BuildPayload(
                type,
                sessionId,
                messageId: null,
                text: null,
                new OpenCodeMessageDebugContext(
                    sessionId,
                    requestedSessionId,
                    runtimeFingerprint,
                    poolFingerprint,
                    resumeMode,
                    DateTime.UtcNow),
                includeLegacyFlags: true)));
    }

    public static CliMessage CreateAssistantMessage(
        string sessionId,
        string text,
        string? messageId = null,
        OpenCodeMessageDebugContext? debugContext = null)
    {
        return new CliMessage(
            "assistant",
            JsonSerializer.SerializeToElement(BuildPayload("assistant", sessionId, messageId, text, debugContext)));
    }

    public static CliMessage CreateTerminalCompletedMessage(
        string sessionId,
        string text,
        string? messageId = null,
        OpenCodeMessageDebugContext? debugContext = null)
    {
        return new CliMessage(
            "terminal.completed",
            JsonSerializer.SerializeToElement(BuildPayload(
                "terminal.completed",
                sessionId,
                messageId,
                text,
                debugContext,
                stopReason: "end_turn")));
    }

    private static Dictionary<string, object?> BuildPayload(
        string type,
        string sessionId,
        string? messageId,
        string? text,
        OpenCodeMessageDebugContext? debugContext,
        string? stopReason = null,
        bool includeLegacyFlags = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["session_id"] = sessionId,
            ["sessionId"] = sessionId,
        };
        AddIfNotEmpty(payload, "message_id", messageId);
        AddIfNotEmpty(payload, "messageId", messageId);
        AddIfNotEmpty(payload, "text", text);
        AddIfNotEmpty(payload, "stop_reason", stopReason);

        if (debugContext != null)
        {
            AddIfNotEmpty(payload, "requested_session_id", debugContext.RequestedSessionId);
            AddIfNotEmpty(payload, "requestedSessionId", debugContext.RequestedSessionId);
            AddIfNotEmpty(payload, "runtimeFingerprint", debugContext.RuntimeFingerprint);
            AddIfNotEmpty(payload, "poolFingerprint", debugContext.PoolFingerprint);
            AddIfNotEmpty(payload, "resumeMode", debugContext.ResumeMode);
            payload["eventTimestamp"] = debugContext.EventTimestampUtc.ToString("O");

            if (includeLegacyFlags)
            {
                payload["resumed"] = string.Equals(debugContext.ResumeMode, "resumed", StringComparison.OrdinalIgnoreCase);
                payload["restarted"] = string.Equals(debugContext.ResumeMode, "restarted", StringComparison.OrdinalIgnoreCase);
            }
        }

        return payload;
    }

    private static void AddIfNotEmpty(Dictionary<string, object?> payload, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            payload[key] = value;
        }
    }
}

internal sealed record OpenCodeMessageDebugContext(
    string SessionId,
    string? RequestedSessionId,
    string? RuntimeFingerprint,
    string? PoolFingerprint,
    string? ResumeMode,
    DateTime EventTimestampUtc);
