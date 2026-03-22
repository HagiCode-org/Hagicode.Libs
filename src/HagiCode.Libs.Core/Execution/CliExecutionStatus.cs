namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Represents the normalized outcome of a CLI execution.
/// </summary>
public enum CliExecutionStatus
{
    /// <summary>
    /// The command completed successfully.
    /// </summary>
    Success = 0,

    /// <summary>
    /// The command completed with a non-zero exit code.
    /// </summary>
    Failed = 1,

    /// <summary>
    /// The command exceeded its timeout and was terminated.
    /// </summary>
    TimedOut = 2,

    /// <summary>
    /// The command was rejected before process creation.
    /// </summary>
    Rejected = 3,

    /// <summary>
    /// A streaming execution completed and produced a terminal envelope.
    /// </summary>
    StreamingCompleted = 4,

    /// <summary>
    /// The command was cancelled by the caller.
    /// </summary>
    Cancelled = 5
}
