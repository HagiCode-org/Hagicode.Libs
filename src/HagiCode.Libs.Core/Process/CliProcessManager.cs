using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using HagiCode.Libs.Core.Discovery;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Creates, executes, interrupts, and terminates CLI subprocesses.
/// </summary>
public class CliProcessManager
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly CliExecutableResolver _executableResolver;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliProcessManager" /> class.
    /// </summary>
    public CliProcessManager()
        : this(new CliExecutableResolver(), TimeProvider.System)
    {
    }

    internal CliProcessManager(CliExecutableResolver executableResolver, TimeProvider timeProvider)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Creates a <see cref="ProcessStartInfo" /> from a start context.
    /// </summary>
    /// <param name="context">The process start context.</param>
    /// <returns>The configured start info.</returns>
    public virtual ProcessStartInfo CreateStartInfo(ProcessStartContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ExecutablePath);

        var resolvedExecutablePath = ResolveExecutablePath(context.ExecutablePath, context.EnvironmentVariables);
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = context.InputEncoding,
            StandardOutputEncoding = context.OutputEncoding,
            StandardErrorEncoding = context.OutputEncoding,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(context.WorkingDirectory))
        {
            startInfo.WorkingDirectory = context.WorkingDirectory;
        }

        ConfigureExecutable(startInfo, context, resolvedExecutablePath);
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
        ArgumentNullException.ThrowIfNull(context);

        var process = new System.Diagnostics.Process { StartInfo = CreateStartInfo(context) };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{context.ExecutablePath}'.");
            }

            return ValueTask.FromResult(new CliProcessHandle(
                process,
                process.StandardInput,
                process.StandardOutput,
                process.StandardError));
        }
        catch
        {
            process.Dispose();
            throw;
        }
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

        var startedAtUtc = _timeProvider.GetUtcNow();
        await using var handle = await StartAsync(context, cancellationToken);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var capturedOutput = new ConcurrentQueue<ProcessOutputChunk>();

        var stdoutTask = CaptureStreamAsync(handle.StandardOutput, ProcessOutputChannel.StandardOutput, standardOutput, capturedOutput, CancellationToken.None);
        var stderrTask = CaptureStreamAsync(handle.StandardError, ProcessOutputChannel.StandardError, standardError, capturedOutput, CancellationToken.None);

        var timedOut = false;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (context.Timeout is { } timeout)
        {
            linkedCts.CancelAfter(timeout);
        }

        try
        {
            await handle.Process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && context.Timeout is not null)
        {
            timedOut = true;
            await StopProcessAsync(handle, disposeHandle: false, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(handle, disposeHandle: false, CancellationToken.None);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);

        var completedAtUtc = _timeProvider.GetUtcNow();
        return new ProcessResult(
            handle.Process.ExitCode,
            standardOutput.ToString(),
            AppendTimeoutMessage(standardError.ToString(), timedOut))
        {
            TimedOut = timedOut,
            CommandPreview = CommandPreviewFormatter.Format(context.ExecutablePath, context.Arguments),
            ResolvedExecutablePath = handle.Process.StartInfo.FileName,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            CapturedOutput = capturedOutput.ToArray()
        };
    }

    /// <summary>
    /// Attempts to interrupt a running subprocess.
    /// </summary>
    /// <param name="handle">The running process handle.</param>
    /// <param name="cancellationToken">Cancels the interrupt request.</param>
    public virtual async Task InterruptAsync(CliProcessHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        await TryInterruptAsync(handle, cancellationToken);
    }

    /// <summary>
    /// Stops a running subprocess and kills the entire process tree if required.
    /// </summary>
    /// <param name="handle">The running process handle.</param>
    /// <param name="cancellationToken">Cancels the stop operation.</param>
    public virtual Task StopAsync(CliProcessHandle? handle, CancellationToken cancellationToken = default)
    {
        if (handle is null)
        {
            return Task.CompletedTask;
        }

        return StopProcessAsync(handle, disposeHandle: true, cancellationToken);
    }

    internal Task StopForExecutionAsync(CliProcessHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return StopProcessAsync(handle, disposeHandle: false, cancellationToken);
    }

    /// <summary>
    /// Gets a value indicating whether the current platform is Windows.
    /// </summary>
    protected virtual bool IsWindows() => OperatingSystem.IsWindows();

    /// <summary>
    /// Resolves an executable path before launch.
    /// </summary>
    protected virtual string ResolveExecutablePath(
        string executablePath,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        return _executableResolver.ResolveExecutablePath(executablePath, environmentVariables) ?? executablePath;
    }

    private void ConfigureExecutable(ProcessStartInfo startInfo, ProcessStartContext context, string resolvedExecutablePath)
    {
        if (IsWindows() && IsBatchFile(resolvedExecutablePath))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(resolvedExecutablePath);
        }
        else
        {
            startInfo.FileName = resolvedExecutablePath;
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

        foreach (var entry in environmentVariables)
        {
            if (entry.Value is null)
            {
                startInfo.Environment.Remove(entry.Key);
                continue;
            }

            startInfo.Environment[entry.Key] = entry.Value;
        }
    }

    private async Task StopProcessAsync(CliProcessHandle handle, bool disposeHandle, CancellationToken cancellationToken)
    {
        try
        {
            if (!handle.Process.HasExited)
            {
                await TryInterruptAsync(handle, cancellationToken);
                TryCloseInput(handle);

                using var gracefulWaitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gracefulWaitCts.CancelAfter(GracefulStopTimeout);
                try
                {
                    await handle.Process.WaitForExitAsync(gracefulWaitCts.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

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
            if (disposeHandle)
            {
                await handle.DisposeAsync();
            }
        }
    }

    private async Task<bool> TryInterruptAsync(CliProcessHandle handle, CancellationToken cancellationToken)
    {
        if (handle.Process.HasExited)
        {
            return false;
        }

        var wroteControlCharacter = await TryWriteControlCharacterAsync(handle, cancellationToken);
        if (!IsWindows())
        {
            var sentSignal = await TrySendInterruptSignalAsync(handle.Process.Id, cancellationToken);
            return wroteControlCharacter || sentSignal;
        }

        return wroteControlCharacter;
    }

    private static async Task<bool> TryWriteControlCharacterAsync(CliProcessHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            await handle.StandardInput.WriteAsync("\u0003".AsMemory(), cancellationToken);
            await handle.StandardInput.FlushAsync(cancellationToken);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryCloseInput(CliProcessHandle handle)
    {
        try
        {
            handle.StandardInput.Close();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static async Task<bool> TrySendInterruptSignalAsync(int processId, CancellationToken cancellationToken)
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
            signalProcess.StartInfo.ArgumentList.Add(processId.ToString());
            if (!signalProcess.Start())
            {
                return false;
            }

            await signalProcess.WaitForExitAsync(cancellationToken);
            return signalProcess.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task CaptureStreamAsync(
        StreamReader reader,
        ProcessOutputChannel channel,
        StringBuilder destination,
        ConcurrentQueue<ProcessOutputChunk> capturedOutput,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            var text = new string(buffer, 0, read);
            destination.Append(text);
            capturedOutput.Enqueue(new ProcessOutputChunk(channel, text, DateTimeOffset.UtcNow));
        }
    }

    private static string AppendTimeoutMessage(string standardError, bool timedOut)
    {
        if (!timedOut)
        {
            return standardError;
        }

        if (string.IsNullOrWhiteSpace(standardError))
        {
            return "The process timed out and was terminated.";
        }

        return standardError + System.Environment.NewLine + "The process timed out and was terminated.";
    }

    private static bool IsBatchFile(string executablePath)
    {
        var extension = Path.GetExtension(executablePath);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }
}
