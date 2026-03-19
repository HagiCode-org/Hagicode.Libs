using System.Diagnostics;
using System.Text;

namespace HagiCode.Libs.Core.Environment;

/// <summary>
/// Runs shell commands by spawning a subprocess.
/// </summary>
public sealed class ProcessShellCommandRunner : IShellCommandRunner
{
    /// <inheritdoc />
    public async Task<ShellCommandResult> RunAsync(
        string shellPath,
        string script,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shellPath);
        ArgumentNullException.ThrowIfNull(script);

        using var process = new System.Diagnostics.Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shellPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        process.StartInfo.ArgumentList.Add("-lc");
        process.StartInfo.ArgumentList.Add(script);

        try
        {
            if (!process.Start())
            {
                return new ShellCommandResult(-1, string.Empty, $"Failed to start shell '{shellPath}'.");
            }
        }
        catch (Exception ex)
        {
            return new ShellCommandResult(-1, string.Empty, ex.Message);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return new ShellCommandResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new ShellCommandResult(-1, string.Empty, "Timed out while loading shell environment.", true);
        }
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
