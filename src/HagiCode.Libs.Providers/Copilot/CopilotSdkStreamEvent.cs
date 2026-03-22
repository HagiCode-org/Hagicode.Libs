namespace HagiCode.Libs.Providers.Copilot;

internal enum CopilotSdkStreamEventType
{
    SessionStarted = 0,
    SessionResumed = 1,
    SessionReused = 2,
    TextDelta = 3,
    Error = 4,
    Completed = 5,
    ReasoningDelta = 6,
    ToolExecutionStart = 7,
    ToolExecutionEnd = 8,
    RawEvent = 9
}

internal sealed record CopilotSdkStreamEvent(
    CopilotSdkStreamEventType Type,
    string? SessionId = null,
    string? RequestedSessionId = null,
    string? Content = null,
    string? ErrorMessage = null,
    string? ToolName = null,
    string? ToolCallId = null,
    string? RawEventType = null);
