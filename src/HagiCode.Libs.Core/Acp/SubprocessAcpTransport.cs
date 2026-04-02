using System.Runtime.CompilerServices;
using System.Text;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Implements a raw ACP transport over a subprocess stdio channel.
/// </summary>
public sealed class SubprocessAcpTransport : IAcpTransport, IAcpTransportDiagnosticsSource
{
    private const int MaxDiagnosticCharacters = 16 * 1024;

    private readonly CliProcessManager _processManager;
    private readonly ProcessStartContext _startContext;
    private readonly object _diagnosticSync = new();
    private readonly StringBuilder _standardErrorBuffer = new();
    private CliProcessHandle? _processHandle;
    private Task? _standardErrorPumpTask;
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
    public string? GetDiagnosticSummary()
    {
        lock (_diagnosticSync)
        {
            return _standardErrorBuffer.Length == 0
                ? null
                : _standardErrorBuffer.ToString().Trim();
        }
    }

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_processHandle is not null)
        {
            throw new InvalidOperationException("The ACP transport is already connected.");
        }

        _processHandle = await _processManager.StartAsync(_startContext, cancellationToken);
        _standardErrorPumpTask = Task.Run(() => PumpStandardErrorAsync(_processHandle), CancellationToken.None);
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
                    await AwaitStandardErrorPumpAsync().ConfigureAwait(false);
                    var standardError = GetDiagnosticSummary();
                    throw new InvalidOperationException(BuildUnexpectedExitMessage(handle.Process.ExitCode, standardError));
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
        await AwaitStandardErrorPumpAsync().ConfigureAwait(false);
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

    private async Task PumpStandardErrorAsync(CliProcessHandle handle)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await handle.StandardError.ReadLineAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            if (line is null)
            {
                break;
            }

            AppendDiagnosticLine(line);
        }
    }

    private void AppendDiagnosticLine(string line)
    {
        lock (_diagnosticSync)
        {
            if (_standardErrorBuffer.Length > 0)
            {
                _standardErrorBuffer.AppendLine();
            }

            _standardErrorBuffer.Append(line);
            if (_standardErrorBuffer.Length > MaxDiagnosticCharacters)
            {
                _standardErrorBuffer.Remove(0, _standardErrorBuffer.Length - MaxDiagnosticCharacters);
            }
        }
    }

    private async Task AwaitStandardErrorPumpAsync()
    {
        if (_standardErrorPumpTask is null)
        {
            return;
        }

        try
        {
            await _standardErrorPumpTask.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _standardErrorPumpTask = null;
        }
    }

    private static string BuildUnexpectedExitMessage(int exitCode, string? standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return $"The ACP subprocess exited unexpectedly with code {exitCode}.";
        }

        return $"The ACP subprocess exited unexpectedly with code {exitCode}: {standardError}";
    }
}
