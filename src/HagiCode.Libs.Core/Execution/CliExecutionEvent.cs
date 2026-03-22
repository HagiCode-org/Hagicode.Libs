namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Represents one event from a streaming command execution.
/// </summary>
public sealed record CliExecutionEvent
{
    /// <summary>
    /// Gets the event kind.
    /// </summary>
    public required CliExecutionEventKind Kind { get; init; }

    /// <summary>
    /// Gets the captured text for output events.
    /// </summary>
    public string? Text { get; init; }

    /// <summary>
    /// Gets the terminal result for completion events.
    /// </summary>
    public CliExecutionResult? Result { get; init; }

    /// <summary>
    /// Gets the timestamp associated with the event.
    /// </summary>
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}
