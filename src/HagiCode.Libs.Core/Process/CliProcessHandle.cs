namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents a running CLI subprocess with redirected standard streams.
/// </summary>
public sealed class CliProcessHandle : IAsyncDisposable
{
    private bool _disposed;
    private CliOwnedProcessState? _ownedProcessState;
    private readonly Func<CliOwnedProcessState, ValueTask>? _ownedProcessCleanup;

    internal CliProcessHandle(
        System.Diagnostics.Process process,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError,
        CliOwnedProcessState? ownedProcessState = null,
        Func<CliOwnedProcessState, ValueTask>? ownedProcessCleanup = null)
    {
        Process = process;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _ownedProcessState = ownedProcessState;
        _ownedProcessCleanup = ownedProcessCleanup;
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
    /// Gets a value indicating whether the process is still running.
    /// </summary>
    public bool IsRunning => !Process.HasExited;

    /// <summary>
    /// Gets the persisted ownership record associated with the subprocess, when tracking is enabled.
    /// </summary>
    public CliOwnedProcessState? OwnedProcessState => _ownedProcessState;

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
        if (ownedProcessState is not null && _ownedProcessCleanup is not null)
        {
            await _ownedProcessCleanup(ownedProcessState).ConfigureAwait(false);
        }
    }

    internal CliOwnedProcessState? DetachOwnedProcessState()
    {
        var state = _ownedProcessState;
        _ownedProcessState = null;
        return state;
    }

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
