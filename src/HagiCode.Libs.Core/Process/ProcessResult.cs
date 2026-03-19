namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents the result of a completed subprocess execution.
/// </summary>
/// <param name="ExitCode">The subprocess exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
