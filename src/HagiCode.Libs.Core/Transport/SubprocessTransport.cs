using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Core.Process;

namespace HagiCode.Libs.Core.Transport;

/// <summary>
/// Implements <see cref="ICliTransport" /> over a subprocess using JSON lines.
/// </summary>
public sealed class SubprocessTransport : ICliTransport
{
    private readonly CliProcessManager _processManager;
    private readonly ProcessStartContext _startContext;
    private CliProcessHandle? _processHandle;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubprocessTransport" /> class.
    /// </summary>
    /// <param name="processManager">The process manager used to spawn the subprocess.</param>
    /// <param name="startContext">The subprocess start context.</param>
    public SubprocessTransport(CliProcessManager processManager, ProcessStartContext startContext)
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
            throw new InvalidOperationException("The transport is already connected.");
        }

        _processHandle = await _processManager.StartAsync(_startContext, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var handle = EnsureConnected();

        var payload = SerializeMessage(message);
        try
        {
            await handle.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await handle.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            var exitException = await TryCreateUnexpectedExitExceptionAsync(handle, cancellationToken, ex).ConfigureAwait(false);
            if (exitException is not null)
            {
                throw exitException;
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CliMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var handle = EnsureConnected();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await handle.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                await WaitBrieflyForExitAsync(handle, cancellationToken).ConfigureAwait(false);

                if (handle.Process.HasExited && handle.Process.ExitCode != 0)
                {
                    throw await CreateUnexpectedExitExceptionAsync(handle, cancellationToken).ConfigureAwait(false);
                }

                yield break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            line = TrimUtf8Bom(line);
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement.Clone();
            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("The CLI message is missing a string 'type' property.");
            }

            yield return new CliMessage(typeElement.GetString()!, root);
        }
    }

    /// <inheritdoc />
    public Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return _processManager.InterruptAsync(EnsureConnected(), cancellationToken);
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
        return _processHandle ?? throw new InvalidOperationException("The transport is not connected.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string SerializeMessage(CliMessage message)
    {
        if (message.Content.ValueKind == JsonValueKind.Object
            && message.Content.TryGetProperty("type", out var typeElement)
            && typeElement.ValueKind == JsonValueKind.String
            && string.Equals(typeElement.GetString(), message.Type, StringComparison.Ordinal))
        {
            return message.Content.GetRawText();
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", message.Type);

            if (message.Content.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in message.Content.EnumerateObject())
                {
                    if (property.NameEquals("type"))
                    {
                        continue;
                    }

                    property.WriteTo(writer);
                }
            }
            else if (message.Content.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
            {
                writer.WritePropertyName("content");
                message.Content.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string TrimUtf8Bom(string line)
    {
        return line.Length > 0 && line[0] == '\uFEFF'
            ? line[1..]
            : line;
    }

    private static async Task<InvalidOperationException?> TryCreateUnexpectedExitExceptionAsync(
        CliProcessHandle handle,
        CancellationToken cancellationToken,
        Exception innerException)
    {
        await WaitBrieflyForExitAsync(handle, cancellationToken).ConfigureAwait(false);

        if (!handle.Process.HasExited)
        {
            return null;
        }

        return await CreateUnexpectedExitExceptionAsync(handle, cancellationToken, innerException).ConfigureAwait(false);
    }

    private static async Task<InvalidOperationException> CreateUnexpectedExitExceptionAsync(
        CliProcessHandle handle,
        CancellationToken cancellationToken,
        Exception? innerException = null)
    {
        var standardError = await TryReadStandardErrorAsync(handle, cancellationToken).ConfigureAwait(false);
        return new InvalidOperationException(
            FormatUnexpectedExitMessage(handle.Process.ExitCode, standardError),
            innerException);
    }

    private static async Task<string> TryReadStandardErrorAsync(CliProcessHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            return await handle.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string FormatUnexpectedExitMessage(int exitCode, string standardError)
    {
        return string.IsNullOrWhiteSpace(standardError)
            ? $"The subprocess exited unexpectedly with code {exitCode}."
            : $"The subprocess exited unexpectedly with code {exitCode}: {standardError}";
    }

    private static async Task WaitBrieflyForExitAsync(CliProcessHandle handle, CancellationToken cancellationToken)
    {
        if (handle.Process.HasExited)
        {
            return;
        }

        var waitForExitTask = handle.Process.WaitForExitAsync(cancellationToken);
        var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)).ConfigureAwait(false);
        if (completedTask == waitForExitTask)
        {
            await waitForExitTask.ConfigureAwait(false);
        }
    }
}
