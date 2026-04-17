using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Creates, executes, interrupts, and terminates CLI subprocesses.
/// </summary>
public class CliProcessManager
{
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StopWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly CliExecutableResolver _executableResolver;
    private readonly CliOwnedProcessRegistry _ownedProcessRegistry;
    private readonly CliProcessOwnershipOptions _ownershipOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CliProcessManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CliProcessManager" /> class.
    /// </summary>
    public CliProcessManager()
        : this(
            new CliExecutableResolver(),
            new CliOwnedProcessRegistry(),
            new CliProcessOwnershipOptions(),
            TimeProvider.System,
            NullLogger<CliProcessManager>.Instance)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CliProcessManager" /> class with dependency injection support.
    /// </summary>
    public CliProcessManager(
        CliExecutableResolver executableResolver,
        IOptions<CliProcessOwnershipOptions>? ownershipOptions = null,
        CliOwnedProcessRegistry? ownedProcessRegistry = null,
        TimeProvider? timeProvider = null,
        ILogger<CliProcessManager>? logger = null)
        : this(
            executableResolver,
            ownedProcessRegistry ?? new CliOwnedProcessRegistry(),
            ownershipOptions?.Value ?? new CliProcessOwnershipOptions(),
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<CliProcessManager>.Instance)
    {
    }

    internal CliProcessManager(CliExecutableResolver executableResolver, TimeProvider timeProvider)
        : this(
            executableResolver,
            new CliOwnedProcessRegistry(),
            new CliProcessOwnershipOptions(),
            timeProvider,
            NullLogger<CliProcessManager>.Instance)
    {
    }

    internal CliProcessManager(
        CliExecutableResolver executableResolver,
        CliOwnedProcessRegistry ownedProcessRegistry,
        CliProcessOwnershipOptions ownershipOptions,
        TimeProvider timeProvider,
        ILogger<CliProcessManager> logger)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _ownedProcessRegistry = ownedProcessRegistry ?? throw new ArgumentNullException(nameof(ownedProcessRegistry));
        _ownershipOptions = ownershipOptions ?? throw new ArgumentNullException(nameof(ownershipOptions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    public virtual async ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);

        var process = new System.Diagnostics.Process { StartInfo = CreateStartInfo(context) };
        CliOwnedProcessState? ownedProcessState = null;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{context.ExecutablePath}'.");
            }

            ownedProcessState = await RegisterOwnedProcessAsync(process, context, cancellationToken).ConfigureAwait(false);
            return new CliProcessHandle(
                process,
                process.StandardInput,
                process.StandardOutput,
                process.StandardError,
                ownedProcessState,
                RemoveOwnedProcessRecordAsync);
        }
        catch
        {
            await RemoveOwnedProcessRecordAsync(ownedProcessState).ConfigureAwait(false);
            TryKill(process);
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
        await using var handle = await StartAsync(context, cancellationToken).ConfigureAwait(false);

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
            await handle.Process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && context.Timeout is not null)
        {
            timedOut = true;
            await StopProcessAsync(handle, disposeHandle: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync(handle, disposeHandle: false, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

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
        await TryInterruptAsync(handle, cancellationToken).ConfigureAwait(false);
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
    /// Recovers and cleans up persisted owned subprocess records during host startup.
    /// </summary>
    public virtual async Task<int> RecoverOwnedProcessesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOwnershipPersistenceEnabled())
        {
            return 0;
        }

        var stateFilePath = _ownershipOptions.StateFilePath!;
        IReadOnlyList<CliOwnedProcessState> states;
        try
        {
            states = await _ownedProcessRegistry.ReadAsync(stateFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Deleting corrupted owned-process state file at {StateFilePath}.", stateFilePath);
            await _ownedProcessRegistry.DeleteIfExistsAsync(stateFilePath, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        var recoveredCount = 0;
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = TryGetProcessById(state.Pid);
            try
            {
                if (process is null)
                {
                    continue;
                }

                if (!MatchesOwnedProcessIdentity(process, state))
                {
                    continue;
                }

                _logger.LogInformation(
                    "Terminating recovered owned CLI process {Pid} for provider {ProviderName}.",
                    state.Pid,
                    state.ProviderName);
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)StopWaitTimeout.TotalMilliseconds);
                recoveredCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to terminate recovered owned CLI process {Pid} for provider {ProviderName}.",
                    state.Pid,
                    state.ProviderName);
            }
            finally
            {
                await RemoveOwnedProcessRecordAsync(state).ConfigureAwait(false);
            }
        }

        return recoveredCount;
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

    /// <summary>
    /// Resolves the stderr encoding used when a Windows batch shim is launched.
    /// </summary>
    protected virtual Encoding ResolveWindowsBatchStandardErrorEncoding(Encoding fallbackEncoding)
    {
        ArgumentNullException.ThrowIfNull(fallbackEncoding);

        return TryResolveWindowsBatchStandardErrorEncoding() ?? fallbackEncoding;
    }

    private void ConfigureExecutable(ProcessStartInfo startInfo, ProcessStartContext context, string resolvedExecutablePath)
    {
        if (IsWindows() && IsBatchFile(resolvedExecutablePath))
        {
            startInfo.StandardErrorEncoding = ResolveWindowsBatchStandardErrorEncoding(context.OutputEncoding);
        }

        startInfo.FileName = resolvedExecutablePath;

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
                await TryInterruptAsync(handle, cancellationToken).ConfigureAwait(false);
                TryCloseInput(handle);

                using var gracefulWaitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                gracefulWaitCts.CancelAfter(GracefulStopTimeout);
                try
                {
                    await handle.Process.WaitForExitAsync(gracefulWaitCts.Token).ConfigureAwait(false);
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
                await handle.Process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            var ownedProcessState = handle.DetachOwnedProcessState();
            await RemoveOwnedProcessRecordAsync(ownedProcessState).ConfigureAwait(false);
            if (disposeHandle)
            {
                await handle.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> TryInterruptAsync(CliProcessHandle handle, CancellationToken cancellationToken)
    {
        if (handle.Process.HasExited)
        {
            return false;
        }

        var wroteControlCharacter = await TryWriteControlCharacterAsync(handle, cancellationToken).ConfigureAwait(false);
        if (!IsWindows())
        {
            var sentSignal = await TrySendInterruptSignalAsync(handle.Process.Id, cancellationToken).ConfigureAwait(false);
            return wroteControlCharacter || sentSignal;
        }

        return wroteControlCharacter;
    }

    private static async Task<bool> TryWriteControlCharacterAsync(CliProcessHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            await handle.StandardInput.WriteAsync("\u0003".AsMemory(), cancellationToken).ConfigureAwait(false);
            await handle.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
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

            await signalProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
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
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<CliOwnedProcessState?> RegisterOwnedProcessAsync(
        System.Diagnostics.Process process,
        ProcessStartContext context,
        CancellationToken cancellationToken)
    {
        if (!IsOwnershipPersistenceEnabled() || context.Ownership is null)
        {
            return null;
        }

        if (!TryGetProcessStartTimeUtc(process, out var startedAtUtc))
        {
            throw new InvalidOperationException(
                $"Failed to read process start time for provider '{context.Ownership.ProviderName}' ownership persistence.");
        }

        var state = new CliOwnedProcessState(
            process.Id,
            startedAtUtc,
            process.StartInfo.FileName,
            context.WorkingDirectory,
            context.Ownership.ProviderName,
            _timeProvider.GetUtcNow());
        await _ownedProcessRegistry.AddOrUpdateAsync(_ownershipOptions.StateFilePath!, state, cancellationToken).ConfigureAwait(false);
        return state;
    }

    private async ValueTask RemoveOwnedProcessRecordAsync(CliOwnedProcessState? state)
    {
        if (state is null || !IsOwnershipPersistenceEnabled())
        {
            return;
        }

        try
        {
            await _ownedProcessRegistry.RemoveAsync(_ownershipOptions.StateFilePath!, state).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Deleting corrupted owned-process state file at {StateFilePath}.", _ownershipOptions.StateFilePath);
            await _ownedProcessRegistry.DeleteIfExistsAsync(_ownershipOptions.StateFilePath!, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool MatchesOwnedProcessIdentity(System.Diagnostics.Process process, CliOwnedProcessState state)
    {
        if (process.HasExited)
        {
            return false;
        }

        if (!TryGetProcessStartTimeUtc(process, out var actualStartedAtUtc))
        {
            _logger.LogWarning(
                "Skipping owned-process recovery for provider {ProviderName} because process {Pid} start time could not be read.",
                state.ProviderName,
                state.Pid);
            return false;
        }

        return (actualStartedAtUtc - state.StartedAtUtc).Duration() <= _ownershipOptions.StartTimeTolerance;
    }

    private bool IsOwnershipPersistenceEnabled()
    {
        return _ownershipOptions.Enabled && !string.IsNullOrWhiteSpace(_ownershipOptions.StateFilePath);
    }

    private static System.Diagnostics.Process? TryGetProcessById(int pid)
    {
        try
        {
            return System.Diagnostics.Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryGetProcessStartTimeUtc(System.Diagnostics.Process process, out DateTimeOffset startedAtUtc)
    {
        try
        {
            startedAtUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return true;
        }
        catch
        {
            startedAtUtc = default;
            return false;
        }
    }

    private static void TryKill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit((int)StopWaitTimeout.TotalMilliseconds);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Encoding? TryResolveWindowsBatchStandardErrorEncoding()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var codePage = GetConsoleOutputCP();
            if (codePage == 0)
            {
                codePage = GetOEMCP();
            }

            return codePage == 0 ? null : Encoding.GetEncoding((int)codePage);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetConsoleOutputCP();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();
}
