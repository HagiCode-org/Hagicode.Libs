using HagiCode.Libs.Core.Process.Windows;

namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents a running CLI subprocess with redirected standard streams.
/// </summary>
public sealed class CliProcessHandle : IAsyncDisposable
{
    private bool _disposed;
    private CliOwnedProcessState? _ownedProcessState;
    private readonly Func<CliOwnedProcessState, ValueTask>? _ownedProcessCleanup;
    private readonly string? _resolvedExecutablePath;
    private readonly IWindowsProcessJob? _containment;
    private readonly Func<ValueTask>? _ledgerRelease;

    internal CliProcessHandle(
        System.Diagnostics.Process process,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError,
        CliOwnedProcessState? ownedProcessState = null,
        Func<CliOwnedProcessState, ValueTask>? ownedProcessCleanup = null,
        string? resolvedExecutablePath = null,
        DateTimeOffset? startedAtUtc = null,
        IWindowsProcessJob? containment = null,
        Func<ValueTask>? ledgerRelease = null)
    {
        Process = process;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _resolvedExecutablePath = resolvedExecutablePath;
        _ownedProcessState = ownedProcessState;
        _ownedProcessCleanup = ownedProcessCleanup;
        _containment = containment;
        _ledgerRelease = ledgerRelease;

        // Read once, right after Start(), so the values stay available after the process exits or Process is disposed.
        Pid = process.Id;
        StartedAtUtc = startedAtUtc;
    }

    /// <summary>
    /// Gets the underlying process instance.
    /// </summary>
    public System.Diagnostics.Process Process { get; }

    /// <summary>
    /// Gets the redirected standard input stream.
    /// </summary>
    public StreamWriter StandardInput { get; }

    /// <summary>
    /// Gets the redirected standard output stream.
    /// </summary>
    public StreamReader StandardOutput { get; }

    /// <summary>
    /// Gets the redirected standard error stream.
    /// </summary>
    public StreamReader StandardError { get; }

    /// <summary>
    /// Gets the path of the CLI that was resolved. For a command script this is the script, not <c>cmd.exe</c> that runs it.
    /// </summary>
    public string ResolvedExecutablePath => _resolvedExecutablePath ?? Process.StartInfo.FileName;

    /// <summary>
    /// Gets a value indicating whether the process is still running.
    /// </summary>
    public bool IsRunning => !Process.HasExited;

    /// <summary>
    /// Gets the persisted ownership record associated with the subprocess, when tracking is enabled.
    /// </summary>
    public CliOwnedProcessState? OwnedProcessState => _ownedProcessState;

    /// <summary>
    /// Gets the process id, captured right after the process started.
    /// </summary>
    public int Pid { get; }

    /// <summary>
    /// Gets the process start time in UTC, captured right after the process started. <see langword="null" /> only when the operating system refused to report it.
    /// </summary>
    public DateTimeOffset? StartedAtUtc { get; }

    /// <summary>
    /// Gets the process identity, or <see langword="null" /> when the start time could not be read.
    /// </summary>
    public ProcessIdentity? Identity => StartedAtUtc is { } startedAtUtc ? new ProcessIdentity(Pid, startedAtUtc) : null;

    /// <summary>
    /// Gets a value indicating whether the process is held in a Windows Job Object that ends its members on stop. A fallback process reports <see langword="false" />.
    /// </summary>
    public bool IsContained => _containment is not null;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var ownedProcessState = DetachOwnedProcessState();
        TryDispose(StandardInput);
        TryDispose(StandardOutput);
        TryDispose(StandardError);
        Process.Dispose();

        // Closing a contained Job ends any member still running, which is the containment guarantee for handles.
        _containment?.Dispose();

        if (ownedProcessState is not null && _ownedProcessCleanup is not null)
        {
            await _ownedProcessCleanup(ownedProcessState).ConfigureAwait(false);
        }

        if (_ledgerRelease is not null)
        {
            await _ledgerRelease().ConfigureAwait(false);
        }
    }

    internal CliOwnedProcessState? DetachOwnedProcessState()
    {
        var state = _ownedProcessState;
        _ownedProcessState = null;
        return state;
    }

    internal IWindowsProcessJob? Containment => _containment;

    private static void TryDispose(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }
}
