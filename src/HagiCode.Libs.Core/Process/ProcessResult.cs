namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents the result of a completed subprocess execution.
/// </summary>
/// <param name="ExitCode">The subprocess exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>
    /// Gets a value indicating whether the process exceeded its timeout.
    /// </summary>
    public bool TimedOut { get; init; }

    /// <summary>
    /// Gets the display-friendly command preview.
    /// </summary>
    public string CommandPreview { get; init; } = string.Empty;

    /// <summary>
    /// Gets the resolved executable path used to launch the process.
    /// </summary>
    public string? ResolvedExecutablePath { get; init; }

    /// <summary>
    /// Gets the execution start timestamp.
    /// </summary>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>
    /// Gets the execution completion timestamp.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; init; }

    /// <summary>
    /// Gets the buffered output chunks captured during execution.
    /// </summary>
    public IReadOnlyList<ProcessOutputChunk> CapturedOutput { get; init; } = [];
}
