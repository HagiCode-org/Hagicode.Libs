namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Represents the normalized output of a CLI execution.
/// </summary>
public sealed record CliExecutionResult
{
    /// <summary>
    /// Gets the normalized execution status.
    /// </summary>
    public required CliExecutionStatus Status { get; init; }

    /// <summary>
    /// Gets the process exit code when available.
    /// </summary>
    public int? ExitCode { get; init; }

    /// <summary>
    /// Gets the display-friendly command preview.
    /// </summary>
    public required string CommandPreview { get; init; }

    /// <summary>
    /// Gets the buffered standard output.
    /// </summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>
    /// Gets the buffered standard error.
    /// </summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>
    /// Gets the capture diagnostics.
    /// </summary>
    public IReadOnlyList<CliExecutionDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets the captured output chunks.
    /// </summary>
    public IReadOnlyList<CliExecutionOutputChunk> CapturedOutput { get; init; } = [];

    /// <summary>
    /// Gets the execution start timestamp.
    /// </summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// Gets the execution completion timestamp.
    /// </summary>
    public DateTimeOffset CompletedAtUtc { get; init; }

    /// <summary>
    /// Gets the execution duration.
    /// </summary>
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;

    /// <summary>
    /// Gets the execution mode used to produce the result.
    /// </summary>
    public CliExecutionMode Mode { get; init; } = CliExecutionMode.Buffered;

    /// <summary>
    /// Gets a value indicating whether the command timed out.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Gets a value indicating whether the result represents a completed execution.
    /// </summary>
    public bool IsSuccess => Status is CliExecutionStatus.Success or CliExecutionStatus.StreamingCompleted;
}
