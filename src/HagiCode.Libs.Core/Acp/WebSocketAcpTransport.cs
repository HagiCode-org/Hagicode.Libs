using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Implements a raw ACP transport over WebSocket.
/// </summary>
public sealed class WebSocketAcpTransport : IAcpTransport
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly Uri _serverUri;
    private readonly int _receiveBufferSize;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebSocketAcpTransport"/> class.
    /// </summary>
    /// <param name="serverUri">The ACP WebSocket endpoint.</param>
    /// <param name="receiveBufferSize">The receive buffer size.</param>
    public WebSocketAcpTransport(Uri serverUri, int receiveBufferSize = 4096)
    {
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
        _receiveBufferSize = receiveBufferSize;
    }

    /// <inheritdoc />
    public bool IsConnected => _webSocket.State == WebSocketState.Open && _disposed == 0;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_webSocket.State == WebSocketState.Open)
        {
            throw new InvalidOperationException("The ACP transport is already connected.");
        }

        await _webSocket.ConnectAsync(_serverUri, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        EnsureConnected();

        var buffer = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ReceiveMessagesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureConnected();

        var rentedBuffer = ArrayPool<byte>.Shared.Rent(_receiveBufferSize);
        try
        {
            using var stream = new MemoryStream();
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(rentedBuffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                    yield break;
                }

                if (result.Count > 0)
                {
                    stream.Write(rentedBuffer, 0, result.Count);
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                if (stream.Length == 0)
                {
                    continue;
                }

                yield return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                stream.SetLength(0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed != 0)
        {
            return;
        }

        if (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseReceived)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _webSocket.Dispose();
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The ACP transport is not connected.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }
}
