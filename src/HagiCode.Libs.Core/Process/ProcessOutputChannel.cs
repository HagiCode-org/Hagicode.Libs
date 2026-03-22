namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Identifies which redirected stream produced a captured output chunk.
/// </summary>
public enum ProcessOutputChannel
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
