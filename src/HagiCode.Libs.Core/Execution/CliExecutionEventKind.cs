namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Defines the kinds of events emitted during streaming execution.
/// </summary>
public enum CliExecutionEventKind
{
    /// <summary>
    /// Buffered text received from standard output.
    /// </summary>
    StandardOutput = 0,

    /// <summary>
    /// Buffered text received from standard error.
    /// </summary>
    StandardError = 1,

    /// <summary>
    /// The terminal result envelope for the execution.
    /// </summary>
    Completed = 2
}
