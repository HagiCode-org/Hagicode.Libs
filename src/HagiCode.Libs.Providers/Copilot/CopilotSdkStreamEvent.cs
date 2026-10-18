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
    StreamingDelta = 7,
    ToolExecutionStart = 8,
    ToolExecutionEnd = 9,
    RawEvent = 10,
    AssistantSnapshot = 11,
    QuestionRequested = 12,
    QuestionClosed = 13,
    Diagnostic = 14
}

internal sealed record CopilotSdkStreamEvent(
    CopilotSdkStreamEventType Type,
    string? SessionId = null,
    string? RequestedSessionId = null,
    string? Content = null,
    string? ErrorMessage = null,
    string? ToolName = null,
    string? ToolCallId = null,
    string? RawEventType = null,
    string? ReasoningId = null,
    bool IsAuthoritativeReasoningReplay = false,
    double? TotalResponseSizeBytes = null,
    bool? ToolSucceeded = null,
    string? QuestionId = null,
    string? Question = null,
    IReadOnlyList<string>? Choices = null,
    bool AllowFreeform = false);
