using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Codex;

internal sealed class CodexExecTransport : ICliTransport
{
    private readonly CliProcessManager _processManager;
    private readonly ProcessStartContext _startContext;
    private CliProcessHandle? _processHandle;
    private bool _disposed;
    private bool _promptSent;

    public CodexExecTransport(CliProcessManager processManager, ProcessStartContext startContext)
    {
        _processManager = processManager ?? throw new ArgumentNullException(nameof(processManager));
        _startContext = startContext ?? throw new ArgumentNullException(nameof(startContext));
    }

    public bool IsConnected => _processHandle is not null && _processHandle.IsRunning;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_processHandle is not null)
        {
            throw new InvalidOperationException("The transport is already connected.");
        }

        _processHandle = await _processManager.StartAsync(_startContext, cancellationToken);
    }

    public async Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var handle = EnsureConnected();

        if (_promptSent)
        {
            throw new InvalidOperationException("Codex exec transport only supports a single prompt per process.");
        }

        var prompt = ExtractPrompt(message.Content);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidDataException("The Codex prompt payload is missing an 'input' string value.");
        }

        await handle.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
        await handle.StandardInput.FlushAsync(cancellationToken);
        handle.StandardInput.Close();
        _promptSent = true;
    }

    public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
                        $"The subprocess exited unexpectedly with code {handle.Process.ExitCode}: {standardError}");
                }

                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The CLI message is missing a string 'type' property.");
            }

            yield return new CliMessage(typeElement.GetString()!, root);
        }
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _processManager.InterruptAsync(EnsureConnected(), cancellationToken);
    }

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

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync();
    }

    private static string? ExtractPrompt(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (content.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.String)
        {
            return inputElement.GetString();
        }

        if (content.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString();
        }

        return null;
    }

    private CliProcessHandle EnsureConnected()
    {
        return _processHandle ?? throw new InvalidOperationException("The transport is not connected.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
