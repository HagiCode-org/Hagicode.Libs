using System.Text.Json;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Kimi;

namespace HagiCode.Libs.Providers.DeepAgents;

internal static class DeepAgentsAcpMessageMapper
{
    public static CliMessage CreateSessionLifecycleMessage(AcpSessionHandle sessionHandle)
        => KimiAcpMessageMapper.CreateSessionLifecycleMessage(sessionHandle);

    public static CliMessage CreateTerminalMessage(string sessionId, JsonElement promptResult)
        => KimiAcpMessageMapper.CreateTerminalMessage(sessionId, promptResult);

    public static CliMessage CreateAssistantMessage(string sessionId, string? text, JsonElement? rawPayload = null)
        => KimiAcpMessageMapper.CreateAssistantMessage(sessionId, text, rawPayload);

    public static CliMessage CreateTerminalFailureMessage(string sessionId, Exception exception)
        => KimiAcpMessageMapper.CreateTerminalFailureMessage(sessionId, exception);

    public static IReadOnlyList<CliMessage> NormalizeNotification(AcpNotification notification)
        => KimiAcpMessageMapper.NormalizeNotification(notification);

    public static bool ShouldPreferPromptCompletedNotification(JsonElement promptResult)
        => KimiAcpMessageMapper.ShouldPreferPromptCompletedNotification(promptResult);

    public static bool IsFailurePromptResult(JsonElement promptResult)
        => KimiAcpMessageMapper.IsFailurePromptResult(promptResult);

    public static bool TryExtractPromptResultText(JsonElement promptResult, out string? text)
        => KimiAcpMessageMapper.TryExtractPromptResultText(promptResult, out text);

    public static bool TryExtractMessageText(JsonElement content, out string? text)
        => KimiAcpMessageMapper.TryExtractMessageText(content, out text);

    public static bool IsReplayAssistantNotification(AcpNotification notification)
        => KimiAcpMessageMapper.IsReplayAssistantNotification(notification);
}
