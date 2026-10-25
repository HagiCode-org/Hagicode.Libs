using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.CiSetup.Console;

/// <summary>
/// Runs install commands through <see cref="CliProcessManager" />.
/// </summary>
public sealed class ProcessCiSetupCommandRunner(CliProcessManager processManager) : ICiSetupCommandRunner
{
    private readonly CliProcessManager _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));

    /// <inheritdoc />
    public async Task<CiSetupCommandResult> RunAsync(CliInstallCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var result = await _processManager.ExecuteAsync(
            new ProcessStartContext
            {
                ExecutablePath = command.FileName,
                Arguments = command.Arguments,
                EnvironmentVariables = command.EnvironmentVariables,
                ApplyResourceLimits = false
            },
            cancellationToken).ConfigureAwait(false);
        return new CiSetupCommandResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
