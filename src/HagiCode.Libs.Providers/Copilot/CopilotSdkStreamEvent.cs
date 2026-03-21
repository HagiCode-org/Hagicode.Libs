namespace HagiCode.Libs.Providers.Copilot;

internal enum CopilotSdkStreamEventType
{
    TextDelta = 0,
    Error = 1,
    Completed = 2,
    ReasoningDelta = 3,
    ToolExecutionStart = 4,
    ToolExecutionEnd = 5,
    RawEvent = 6
}

internal sealed record CopilotSdkStreamEvent(
    CopilotSdkStreamEventType Type,
    string? Content = null,
    string? ErrorMessage = null,
    string? ToolName = null,
    string? ToolCallId = null,
    string? RawEventType = null);
