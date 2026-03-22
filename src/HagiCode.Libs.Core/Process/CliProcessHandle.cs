namespace HagiCode.Libs.Core.Process;

/// <summary>
/// Represents a running CLI subprocess with redirected standard streams.
/// </summary>
public sealed class CliProcessHandle : IAsyncDisposable
{
    private bool _disposed;

    internal CliProcessHandle(System.Diagnostics.Process process, StreamWriter standardInput, StreamReader standardOutput, StreamReader standardError)
    {
        Process = process;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
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

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
        return ValueTask.CompletedTask;
    }
}
