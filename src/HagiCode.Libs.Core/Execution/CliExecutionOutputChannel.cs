namespace HagiCode.Libs.Core.Execution;

/// <summary>
/// Identifies which redirected stream produced a captured output chunk.
/// </summary>
public enum CliExecutionOutputChannel
{
    /// <summary>
    /// Standard output.
    /// </summary>
    StandardOutput = 0,

    /// <summary>
    /// Standard error.
    /// </summary>
    StandardError = 1
}
