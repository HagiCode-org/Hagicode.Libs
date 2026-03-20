using System.Runtime.CompilerServices;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Implements a raw ACP transport over a subprocess stdio channel.
/// </summary>
public sealed class SubprocessAcpTransport : IAcpTransport
{
    private readonly CliProcessManager _processManager;
    private readonly ProcessStartContext _startContext;
    private CliProcessHandle? _processHandle;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubprocessAcpTransport" /> class.
    /// </summary>
    /// <param name="processManager">The process manager.</param>
    /// <param name="startContext">The process start context.</param>
    public SubprocessAcpTransport(CliProcessManager processManager, ProcessStartContext startContext)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _startContext = startContext ?? throw new ArgumentNullException(nameof(startContext));
    }

    /// <inheritdoc />
    public bool IsConnected => _processHandle is not null && _processHandle.IsRunning;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_processHandle is not null)
        {
            throw new InvalidOperationException("The ACP transport is already connected.");
        }

        _processHandle = await _processManager.StartAsync(_startContext, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var handle = EnsureConnected();
        await handle.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
        await handle.StandardInput.FlushAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var handle = EnsureConnected();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await handle.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                if (handle.Process.HasExited && handle.Process.ExitCode != 0)
                {
                    var standardError = await handle.StandardError.ReadToEndAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"The ACP subprocess exited unexpectedly with code {handle.Process.ExitCode}: {standardError}");
                }

                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            yield return line;
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_processHandle is null)
        {
            return;
        }

        var handle = _processHandle;
        _processHandle = null;
        await _processManager.StopAsync(handle, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync();
    }

    private CliProcessHandle EnsureConnected()
    {
        return _processHandle ?? throw new InvalidOperationException("The ACP transport is not connected.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
