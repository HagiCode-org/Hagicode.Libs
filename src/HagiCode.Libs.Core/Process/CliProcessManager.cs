using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process.Platform;
using HagiCode.Libs.Core.Process.Termination;
using HagiCode.Libs.Core.Process.Tree;
using HagiCode.Libs.Core.Process.Windows;
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
    private readonly ICliResourceLimitProvider? _resourceLimitProvider;
    private readonly CgroupScopeCapabilityProbe _cgroupScopeProbe;
    private readonly CliManagedProcessLedger _ledger;
    private readonly IWindowsJobFactory _jobFactory;
    private readonly IProcessPlatform _processPlatform;
    private readonly ProcessTreeSnapshotter _snapshotter;
    private readonly CliProcessTerminator _terminator;

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
    /// A <paramref name="resourceLimitProvider" /> puts agent launches inside a systemd user scope carrying the memory and/or CPU limit it reports; without one, no limit applies.
    /// A <paramref name="managedProcessLedger" /> shared by several managers makes their managed processes visible through one query; without one, the manager keeps its own ledger.
    /// </summary>
    public CliProcessManager(
        CliExecutableResolver executableResolver,
        IOptions<CliProcessOwnershipOptions>? ownershipOptions = null,
        CliOwnedProcessRegistry? ownedProcessRegistry = null,
        TimeProvider? timeProvider = null,
        ILogger<CliProcessManager>? logger = null,
        ICliResourceLimitProvider? resourceLimitProvider = null,
        CliManagedProcessLedger? managedProcessLedger = null)
        : this(
            executableResolver,
            ownedProcessRegistry ?? new CliOwnedProcessRegistry(),
            ownershipOptions?.Value ?? new CliProcessOwnershipOptions(),
            timeProvider ?? TimeProvider.System,
            logger ?? NullLogger<CliProcessManager>.Instance,
            resourceLimitProvider,
            cgroupScopeProbe: null,
            managedProcessLedger)
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
        ILogger<CliProcessManager> logger,
        ICliResourceLimitProvider? resourceLimitProvider = null,
        CgroupScopeCapabilityProbe? cgroupScopeProbe = null,
        CliManagedProcessLedger? managedProcessLedger = null,
        IWindowsJobFactory? jobFactory = null,
        IProcessPlatform? processPlatform = null)
    {
        _executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        _ownedProcessRegistry = ownedProcessRegistry ?? throw new ArgumentNullException(nameof(ownedProcessRegistry));
        _ownershipOptions = ownershipOptions ?? throw new ArgumentNullException(nameof(ownershipOptions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resourceLimitProvider = resourceLimitProvider;
        _cgroupScopeProbe = cgroupScopeProbe ?? CgroupScopeCapabilityProbe.Default;
        _ledger = managedProcessLedger ?? new CliManagedProcessLedger();
        _jobFactory = jobFactory ?? WindowsJobFactory.Default;
        _processPlatform = processPlatform ?? ProcessPlatformSelector.Default;
        _snapshotter = new ProcessTreeSnapshotter(
            _processPlatform,
            _ownershipOptions.StartTimeTolerance,
            ContainmentMembersOf,
            logger: null);
        _terminator = new CliProcessTerminator(_processPlatform, _ownershipOptions.StartTimeTolerance, _snapshotter);
    }

    /// <summary>
    /// Creates a <see cref="ProcessStartInfo" /> from a start context.
    /// </summary>
    /// <param name="context">The process start context.</param>
    /// <returns>The configured start info.</returns>
    public virtual ProcessStartInfo CreateStartInfo(ProcessStartContext context)
    {
        return CreateStartPlan(context).StartInfo;
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

        var plan = CreateStartPlan(context);
        var process = new System.Diagnostics.Process { StartInfo = plan.StartInfo };
        CliOwnedProcessState? ownedProcessState = null;
        IWindowsProcessJob? containment = null;
        ProcessIdentity? ledgerIdentity = null;
        try
        {
            // The Job exists before the process does, so no child can run outside it; the process joins right after Start().
            containment = CreateContainmentJob(context);
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process '{context.ExecutablePath}'.");
            }

            DateTimeOffset? startedAtUtc = TryGetProcessStartTimeUtc(process, out var readStartedAtUtc) ? readStartedAtUtc : null;
            if (context.Ownership is not null && startedAtUtc is null)
            {
                // The process still starts. It is just not tracked, because its identity cannot be recorded.
                _logger.LogWarning(
                    "Process {Pid} for provider {ProviderName} started, but its start time cannot be read, so it is not tracked as a managed process.",
                    process.Id,
                    context.Ownership.ProviderName);
            }

            containment = AssignContainment(containment, process, startedAtUtc, context.Ownership?.ProviderName);

            ownedProcessState = await RegisterOwnedProcessAsync(process, context, plan.ResolvedExecutablePath, cancellationToken).ConfigureAwait(false);

            Func<ValueTask>? ledgerRelease = null;
            if (context.Ownership is not null && startedAtUtc is { } ledgerStartedAtUtc)
            {
                var identity = new ProcessIdentity(process.Id, ledgerStartedAtUtc);
                _ledger.Add(
                    new CliManagedProcessInfo(
                        identity,
                        context.Ownership.ProviderName,
                        context.Ownership.SessionId,
                        plan.ResolvedExecutablePath,
                        context.WorkingDirectory,
                        CliManagedProcessOrigin.Started,
                        containment is not null),
                    containment);
                ledgerIdentity = identity;
                ledgerRelease = () =>
                {
                    _ledger.Remove(identity);
                    return ValueTask.CompletedTask;
                };
            }

            return new CliProcessHandle(
                process,
                process.StandardInput,
                process.StandardOutput,
                process.StandardError,
                ownedProcessState,
                RemoveOwnedProcessRecordAsync,
                plan.ResolvedExecutablePath,
                startedAtUtc,
                containment,
                ledgerRelease);
        }
        catch
        {
            if (ledgerIdentity is { } registeredIdentity)
            {
                _ledger.Remove(registeredIdentity);
            }

            await RemoveOwnedProcessRecordAsync(ownedProcessState).ConfigureAwait(false);
            TryKill(process);
            containment?.Dispose();
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Ends a managed process and its descendants after confirming the root's identity. Descendants are ended before their parents.
    /// When the manager holds a Windows Job for the root, the Job ends the members instead.
    /// </summary>
    /// <param name="identity">The root's process id and start time, as recorded by the ledger or a snapshot.</param>
    /// <param name="options">Termination options. Defaults are used when <see langword="null" />.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The typed result.</returns>
    public virtual Task<CliProcessTerminationResult> TerminateProcessTreeAsync(
        ProcessIdentity identity,
        CliProcessTerminationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_ledger.TryGet(identity, out var entry) && entry.Containment is { } job)
        {
            return _terminator.TerminateContainedTreeAsync(identity, job, options, cancellationToken);
        }

        return _terminator.TerminateProcessTreeAsync(identity, options, cancellationToken);
    }

    /// <summary>
    /// Ends one process after confirming its identity, without touching its descendants.
    /// </summary>
    /// <param name="identity">The process id and start time recorded earlier.</param>
    /// <param name="options">Termination options. Defaults are used when <see langword="null" />.</param>
    /// <param name="cancellationToken">Cancels the call before it starts.</param>
    /// <returns>The typed result.</returns>
    public virtual Task<CliProcessTerminationResult> TerminateProcessAsync(
        ProcessIdentity identity,
        CliProcessTerminationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _terminator.TerminateProcessAsync(identity, options, cancellationToken);
    }

    /// <summary>
    /// Lists the managed processes that are currently alive. Exited processes and reused process ids are never returned.
    /// </summary>
    /// <param name="query">The provider and session filter. <see langword="null" /> returns every alive managed process.</param>
    /// <returns>The alive managed processes.</returns>
    public virtual IReadOnlyList<CliManagedProcessInfo> GetAliveManagedProcesses(CliManagedProcessQuery? query = null)
    {
        return _ledger.GetAlive(query, _ownershipOptions.StartTimeTolerance);
    }

    /// <summary>
    /// Captures the process trees under the alive managed processes that match the query, in one process-table read.
    /// </summary>
    /// <param name="query">The provider and session filter. <see langword="null" /> captures every alive managed process.</param>
    /// <param name="options">Snapshot options. Defaults are used when <see langword="null" />.</param>
    /// <param name="cancellationToken">Cancels the capture.</param>
    /// <returns>One tree per matching managed process, in ledger order.</returns>
    public virtual async Task<IReadOnlyList<CliManagedProcessTree>> CaptureManagedProcessTreesAsync(
        CliManagedProcessQuery? query = null,
        ProcessTreeSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var entries = _ledger.GetAliveEntries(query, _ownershipOptions.StartTimeTolerance);
        if (entries.Count == 0)
        {
            return [];
        }

        var snapshot = await _snapshotter.CaptureAsync(
            new ProcessTreeSnapshotRequest
            {
                Roots = entries.Select(entry => entry.Info.Identity).ToArray(),
                Options = options ?? new ProcessTreeSnapshotOptions(),
            },
            cancellationToken).ConfigureAwait(false);

        var treesByRoot = snapshot.Roots.ToDictionary(root => root.Root);
        return entries
            .Where(entry => treesByRoot.ContainsKey(entry.Info.Identity))
            .Select(entry => new CliManagedProcessTree(entry.Info, treesByRoot[entry.Info.Identity]))
            .ToArray();
    }

    /// <summary>
    /// Registers a process that the caller started itself, so it appears in <see cref="GetAliveManagedProcesses" /> and can be terminated by identity.
    /// The process is not stopped when the returned registration is disposed.
    /// </summary>
    /// <param name="process">The started process.</param>
    /// <param name="request">The provider, session and reporting metadata.</param>
    /// <param name="cancellationToken">Cancels the registration before it starts.</param>
    /// <returns>The registration. Dispose it to remove the entry.</returns>
    /// <exception cref="InvalidOperationException">The process has exited or its start time cannot be read.</exception>
    public virtual async ValueTask<CliExternalProcessRegistration> RegisterExternalProcessAsync(
        System.Diagnostics.Process process,
        CliExternalProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetProcessStartTimeUtc(process, out var startedAtUtc))
        {
            throw new InvalidOperationException(
                $"Failed to read the start time of process {process.Id} for provider '{request.ProviderName}'. The process may have exited.");
        }

        var identity = new ProcessIdentity(process.Id, startedAtUtc);
        var executablePath = request.ExecutablePath ?? TryGetMainModuleName(process) ?? process.ProcessName;
        var containment = AssignContainment(CreateContainmentJob(request.ProviderName), process, startedAtUtc, request.ProviderName);
        var isContained = containment is not null;

        _ledger.Add(
            new CliManagedProcessInfo(
                identity,
                request.ProviderName,
                request.SessionId,
                executablePath,
                request.WorkingDirectory,
                CliManagedProcessOrigin.External,
                isContained),
            containment);

        CliOwnedProcessState? ownedProcessState = null;
        try
        {
            if (IsOwnershipPersistenceEnabled())
            {
                ownedProcessState = new CliOwnedProcessState(
                    process.Id,
                    startedAtUtc,
                    executablePath,
                    request.WorkingDirectory,
                    request.ProviderName,
                    _timeProvider.GetUtcNow());
                await _ownedProcessRegistry.AddOrUpdateAsync(_ownershipOptions.StateFilePath!, ownedProcessState, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            _ledger.Remove(identity);
            containment?.ReleaseKeepingMembers();
            throw;
        }

        return new CliExternalProcessRegistration(
            identity,
            isContained,
            async () =>
            {
                _ledger.Remove(identity);
                // The process keeps running after the registration is disposed, so the Job lets go of it without ending it.
                containment?.ReleaseKeepingMembers();
                await RemoveOwnedProcessRecordAsync(ownedProcessState).ConfigureAwait(false);
            });
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
        if (context.MaxCapturedOutputCharacters is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), "MaxCapturedOutputCharacters must be zero or greater when set.");
        }

        var startedAtUtc = _timeProvider.GetUtcNow();
        await using var handle = await StartAsync(context, cancellationToken).ConfigureAwait(false);

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        var capturedOutput = new ConcurrentQueue<ProcessOutputChunk>();

        var stdoutTask = CaptureStreamAsync(handle.StandardOutput, ProcessOutputChannel.StandardOutput, standardOutput, capturedOutput, context.MaxCapturedOutputCharacters, CancellationToken.None);
        var stderrTask = CaptureStreamAsync(handle.StandardError, ProcessOutputChannel.StandardError, standardError, capturedOutput, context.MaxCapturedOutputCharacters, CancellationToken.None);

        // One-shot executions never stream request payloads over stdin, so close the pipe early.
        // Some CLIs wait for EOF before terminating even when all request arguments are provided on the command line.
        TryCloseInput(handle);

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
            ResolvedExecutablePath = handle.ResolvedExecutablePath,
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
        var exitWait = new CliProcessTerminationOptions { ExitWaitTimeout = StopWaitTimeout };
        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // The persisted identity is checked inside the terminator, so a reused process id is never killed.
                var result = await TerminateProcessTreeAsync(
                    new ProcessIdentity(state.Pid, state.StartedAtUtc),
                    exitWait,
                    cancellationToken).ConfigureAwait(false);
                LogRecoveryOutcome(state, result);
                if (result.Outcome is CliProcessTerminationOutcome.Terminated or CliProcessTerminationOutcome.PartiallyTerminated)
                {
                    recoveredCount++;
                }
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

    private void LogRecoveryOutcome(CliOwnedProcessState state, CliProcessTerminationResult result)
    {
        switch (result.Outcome)
        {
            case CliProcessTerminationOutcome.Terminated:
                _logger.LogInformation(
                    "Terminated recovered owned CLI process {Pid} and its descendants for provider {ProviderName}.",
                    state.Pid,
                    state.ProviderName);
                break;
            case CliProcessTerminationOutcome.AlreadyExited:
            case CliProcessTerminationOutcome.IdentityMismatch:
                _logger.LogDebug(
                    "Skipped recovered owned CLI process {Pid} for provider {ProviderName}: {Outcome}.",
                    state.Pid,
                    state.ProviderName,
                    result.Outcome);
                break;
            default:
                _logger.LogWarning(
                    "Recovered owned CLI process {Pid} for provider {ProviderName} was not fully terminated ({Outcome}): {Message}",
                    state.Pid,
                    state.ProviderName,
                    result.Outcome,
                    result.Message);
                break;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the current platform is Windows.
    /// </summary>
    protected virtual bool IsWindows() => OperatingSystem.IsWindows();

    private IWindowsProcessJob? CreateContainmentJob(ProcessStartContext context)
    {
        return context.Ownership is null ? null : CreateContainmentJob(context.Ownership.ProviderName);
    }

    // A Job is only created for agent launches with ownership metadata, on Windows, with the kill switch on. Creation failures
    // fall back to the existing kill path and never fail the launch.
    private IWindowsProcessJob? CreateContainmentJob(string providerName)
    {
        if (!_ownershipOptions.WindowsJobContainment || !IsWindows())
        {
            return null;
        }

        try
        {
            return _jobFactory.Create();
        }
        catch (Exception ex) when (ex is Win32Exception or PlatformNotSupportedException)
        {
            _logger.LogWarning(
                ex,
                "Could not create a Windows Job Object for provider {ProviderName}. The process falls back to kill-on-stop without containment. Win32 error {Win32Error}.",
                providerName,
                ex is Win32Exception win32 ? win32.NativeErrorCode : 0);
            return null;
        }
    }

    // A failed assignment disposes the Job, because nothing was placed in it, and returns null so the handle is not contained.
    private IWindowsProcessJob? AssignContainment(
        IWindowsProcessJob? job,
        System.Diagnostics.Process process,
        DateTimeOffset? startedAtUtc,
        string? providerName)
    {
        if (job is null)
        {
            return null;
        }

        if (!job.TryAssignProcess(process.Id, out var win32Error))
        {
            _logger.LogWarning(
                "Could not assign process {Pid} for provider {ProviderName} to a Windows Job Object. The process falls back to kill-on-stop without containment. Win32 error {Win32Error}.",
                process.Id,
                providerName,
                win32Error);
            job.Dispose();
            return null;
        }

        // Descendants spawned before the assignment landed are swept in. A sweep failure keeps the Job, because the root is already contained.
        if (startedAtUtc is { } rootStartedAtUtc)
        {
            try
            {
                WindowsJobSweep.Sweep(
                    job,
                    _processPlatform,
                    process.Id,
                    rootStartedAtUtc,
                    _ownershipOptions.StartTimeTolerance,
                    (pid, error) => _logger.LogWarning(
                        "Could not assign early child process {ChildPid} of process {Pid} for provider {ProviderName} to its Windows Job Object. Win32 error {Win32Error}.",
                        pid,
                        process.Id,
                        providerName,
                        error));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not sweep early child processes of process {Pid} into its Windows Job Object.", process.Id);
            }
        }

        return job;
    }

    private IReadOnlyList<int> ContainmentMembersOf(ProcessIdentity identity)
    {
        if (!_ledger.TryGet(identity, out var entry) || entry.Containment is not { } job)
        {
            return [];
        }

        try
        {
            return job.ListMemberProcessIds();
        }
        catch (ObjectDisposedException)
        {
            return [];
        }
    }

    private static string? TryGetMainModuleName(System.Diagnostics.Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

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

    internal CliProcessStartPlan CreateStartPlan(ProcessStartContext context)
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

        // The start info may now name systemd-run, but the plan keeps the CLI as the resolved path, like the cmd.exe launch.
        if (context.ApplyResourceLimits && _resourceLimitProvider is not null)
        {
            CgroupScopeLauncher.Apply(startInfo, _resourceLimitProvider.GetCurrent(), _cgroupScopeProbe, _logger);
        }

        return new CliProcessStartPlan(startInfo, resolvedExecutablePath);
    }

    private void ConfigureExecutable(ProcessStartInfo startInfo, ProcessStartContext context, string resolvedExecutablePath)
    {
        if (IsWindows() && IsBatchFile(resolvedExecutablePath))
        {
            startInfo.StandardErrorEncoding = ResolveWindowsBatchStandardErrorEncoding(context.OutputEncoding);

            // A command script runs through cmd.exe with a command line that is built and verified here. ArgumentList is not used,
            // because it would re-quote the arguments and break the /s /c form.
            var launch = WindowsCommandScriptLaunch.Create(resolvedExecutablePath, context.Arguments);
            startInfo.FileName = launch.FileName;
            startInfo.Arguments = launch.Arguments;
            return;
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

            // The Job ends the members even when the root exited during the graceful window, because a daemon can outlive its parent.
            TerminateContainment(handle);
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

    private void TerminateContainment(CliProcessHandle handle)
    {
        var containment = handle.Containment;
        if (containment is null)
        {
            return;
        }

        try
        {
            containment.Terminate();
        }
        catch (Exception ex) when (ex is Win32Exception or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not terminate the Windows Job Object of process {Pid}; falling back to a tree kill.", handle.Pid);
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
        int? maxCharacters,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var remaining = maxCharacters ?? long.MaxValue;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            // Past the cap the stream is still read to EOF so the child never blocks on a full pipe.
            if (remaining <= 0)
            {
                continue;
            }

            var keptLength = (int)Math.Min(read, remaining);
            var text = new string(buffer, 0, keptLength);
            remaining -= keptLength;
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
        string resolvedExecutablePath,
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
            resolvedExecutablePath,
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

    // Identity is the recorded Pid plus StartedAtUtc. ExecutablePath is never compared, so a record that names a resolved
    // command script still matches the live cmd.exe that runs it. The terminator performs that comparison.
    private bool IsOwnershipPersistenceEnabled()
    {
        return _ownershipOptions.Enabled && !string.IsNullOrWhiteSpace(_ownershipOptions.StateFilePath);
    }

    // Seam: tests override this to simulate a start time the runtime cannot read.
    internal virtual bool TryGetProcessStartTimeUtc(System.Diagnostics.Process process, out DateTimeOffset startedAtUtc)
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
