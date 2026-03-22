namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Defines how CLI output is surfaced to callers.
/// </summary>
public enum CliExecutionMode
{
    /// <summary>
    /// Waits for process completion and returns a single buffered result.
    /// </summary>
    Buffered = 0,

    /// <summary>
    /// Streams output events while the process is running and then returns a terminal result.
    /// </summary>
    Streaming = 1
}
