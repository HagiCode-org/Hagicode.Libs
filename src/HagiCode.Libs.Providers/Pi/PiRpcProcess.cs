using System.Text;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// The process side of an RPC run: line-oriented standard input and output plus the lifetime of the process. It is the seam
/// that lets the RPC conversation be scripted in tests without a Pi executable.
/// </summary>
internal interface IPiRpcProcess : IAsyncDisposable
{
    /// <summary>
    /// Everything the process writes to standard error. Completes when the stream ends. Reading starts with the process, so a
    /// chatty process never blocks on a full pipe.
    /// </summary>
    Task<string> StandardError { get; }

    /// <summary>
    /// Reads one line of standard output, or <see langword="null" /> at the end of the stream.
    /// </summary>
    ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes one line (terminated by a line feed) to standard input and flushes it. Throws <see cref="IOException" /> when
    /// the process no longer reads its input.
    /// </summary>
    ValueTask WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Closes standard input, which asks Pi for an orderly shutdown. Does nothing when it is already closed.
    /// </summary>
    void CloseInput();

    /// <summary>
    /// Completes with the exit code when the process has exited.
    /// </summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ends the process and its children if it is still running, and releases its streams.
    /// </summary>
    Task StopAsync();
}

/// <summary>
/// The <see cref="IPiRpcProcess" /> of a real process started through <see cref="CliProcessManager" />.
/// </summary>
internal sealed class PiRpcProcessHandle : IPiRpcProcess
{
    private readonly CliProcessManager _processManager;
    private readonly CliProcessHandle _handle;

    public PiRpcProcessHandle(CliProcessManager processManager, CliProcessHandle handle)
    {
        _processManager = processManager;
        _handle = handle;
        StandardError = ReadToEndAsync(handle.StandardError);
    }

    public Task<string> StandardError { get; }

    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        return _handle.StandardOutput.ReadLineAsync(cancellationToken);
    }

    public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        // The protocol is framed on LF only; the writer's own NewLine would be CRLF on Windows.
        var writer = _handle.StandardInput;
        await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public void CloseInput()
    {
        try
        {
            _handle.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _handle.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return _handle.Process.ExitCode;
    }

    public Task StopAsync() => _processManager.StopAsync(_handle, CancellationToken.None);

    public ValueTask DisposeAsync() => _handle.DisposeAsync();

    private static async Task<string> ReadToEndAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        var builder = new StringBuilder();
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                builder.Append(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The handle was disposed while the process was being stopped: keep what was read.
        }

        return builder.ToString();
    }
}
