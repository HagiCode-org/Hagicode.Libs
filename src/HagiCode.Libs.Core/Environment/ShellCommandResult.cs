namespace HagiCode.Libs.Core.Environment;

/// <summary>
/// Represents the result of running a shell command.
/// </summary>
/// <param name="ExitCode">The shell process exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
/// <param name="TimedOut">Indicates whether the command timed out.</param>
public sealed record ShellCommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false);
