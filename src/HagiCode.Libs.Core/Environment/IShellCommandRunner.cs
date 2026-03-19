namespace HagiCode.Libs.Core.Environment;

/// <summary>
/// Runs a shell command to materialize a runtime environment.
/// </summary>
public interface IShellCommandRunner
{
    /// <summary>
    /// Runs a shell command.
    /// </summary>
    /// <param name="shellPath">The shell executable to launch.</param>
    /// <param name="script">The shell script to execute.</param>
    /// <param name="timeout">The maximum time to allow the command to run.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    /// <returns>The command result.</returns>
    Task<ShellCommandResult> RunAsync(
        string shellPath,
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
