using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Creates, executes, interrupts, and terminates CLI subprocesses.
/// </summary>
public class CliProcessManager
{
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Creates a <see cref="ProcessStartInfo" /> from a start context.
    /// </summary>
    /// <param name="context">The process start context.</param>
    /// <returns>The configured start info.</returns>
    public virtual ProcessStartInfo CreateStartInfo(ProcessStartContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ExecutablePath);

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = context.OutputEncoding,
            StandardErrorEncoding = context.OutputEncoding,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(context.WorkingDirectory))
        {
            startInfo.WorkingDirectory = context.WorkingDirectory;
        }

        ConfigureExecutable(startInfo, context);
        ApplyEnvironmentVariables(startInfo, context.EnvironmentVariables);
        return startInfo;
    }

    /// <summary>
    /// Starts a long-running subprocess and returns its redirected streams.
    /// </summary>
    /// <param name="context">The process start context.</param>
    /// <param name="cancellationToken">Cancels the start operation.</param>
    /// <returns>A handle for the running subprocess.</returns>
    public virtual ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var process = new System.Diagnostics.Process { StartInfo = CreateStartInfo(context) };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start process '{context.ExecutablePath}'.");
        }

        return ValueTask.FromResult(new CliProcessHandle(
            process,
            process.StandardInput,
            process.StandardOutput,
            process.StandardError));
    }

    /// <summary>
    /// Executes a subprocess to completion and captures its output.
    /// </summary>
    /// <param name="context">The process start context.</param>
    /// <param name="cancellationToken">Cancels the execution.</param>
    /// <returns>The captured process result.</returns>
    public virtual async Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using var handle = await StartAsync(context, cancellationToken);
        var stdoutTask = handle.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = handle.StandardError.ReadToEndAsync(cancellationToken);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (context.Timeout is { } timeout)
        {
            linkedCts.CancelAfter(timeout);
        }

        try
        {
            await handle.Process.WaitForExitAsync(linkedCts.Token);
            return new ProcessResult(
                handle.Process.ExitCode,
                await stdoutTask,
                await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && context.Timeout is not null)
        {
            await StopAsync(handle, CancellationToken.None);
            var standardOutput = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty;
            var standardError = stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty;
            if (!string.IsNullOrWhiteSpace(standardError))
            {
                standardError += System.Environment.NewLine;
            }

            standardError += "The process timed out and was terminated.";
            return new ProcessResult(-1, standardOutput, standardError);
        }
    }

    /// <summary>
    /// Attempts to interrupt a running subprocess.
    /// </summary>
    /// <param name="handle">The running process handle.</param>
    /// <param name="cancellationToken">Cancels the interrupt request.</param>
    public virtual async Task InterruptAsync(CliProcessHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.Process.HasExited)
        {
            return;
        }

        try
        {
            await handle.StandardInput.WriteAsync("\u0003".AsMemory(), cancellationToken);
            await handle.StandardInput.FlushAsync(cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var signalProcess = new System.Diagnostics.Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "kill",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                signalProcess.StartInfo.ArgumentList.Add("-INT");
                signalProcess.StartInfo.ArgumentList.Add(handle.Process.Id.ToString());
                if (signalProcess.Start())
                {
                    await signalProcess.WaitForExitAsync(cancellationToken);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Stops a running subprocess and kills the entire process tree if required.
    /// </summary>
    /// <param name="handle">The running process handle.</param>
    /// <param name="cancellationToken">Cancels the stop operation.</param>
    public virtual async Task StopAsync(CliProcessHandle? handle, CancellationToken cancellationToken = default)
    {
        if (handle is null)
        {
            return;
        }

        try
        {
            if (!handle.Process.HasExited)
            {
                handle.Process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(StopWaitTimeout);
            if (!handle.Process.HasExited)
            {
                await handle.Process.WaitForExitAsync(waitCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await handle.DisposeAsync();
        }
    }

    /// <summary>
    /// Gets a value indicating whether the current platform is Windows.
    /// </summary>
    protected virtual bool IsWindows() => OperatingSystem.IsWindows();

    private void ConfigureExecutable(ProcessStartInfo startInfo, ProcessStartContext context)
    {
        if (IsWindows() && IsBatchFile(context.ExecutablePath))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(context.ExecutablePath);
        }
        else
        {
            startInfo.FileName = context.ExecutablePath;
        }

        foreach (var argument in context.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static void ApplyEnvironmentVariables(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (environmentVariables is null)
        {
            return;
        }

        foreach (var pair in environmentVariables)
        {
            if (pair.Value is null)
            {
                startInfo.Environment.Remove(pair.Key);
            }
            else
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
    }

    private static bool IsBatchFile(string executablePath)
    {
        var extension = Path.GetExtension(executablePath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }
}
