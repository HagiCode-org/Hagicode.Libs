namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Defines raw JSON-RPC transport operations for ACP-compatible CLIs.
/// </summary>
public interface IAcpTransport : IAsyncDisposable
{
    /// <summary>
    /// Opens the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one raw JSON-RPC payload.
    /// </summary>
    /// <param name="message">The serialized payload.</param>
    /// <param name="cancellationToken">Cancels the send operation.</param>
    Task SendMessageAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams raw payload frames from the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancels the receive loop.</param>
    /// <returns>The received payload frames.</returns>
    IAsyncEnumerable<string> ReceiveMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the transport.
    /// </summary>
    /// <param name="cancellationToken">Cancels the disconnect operation.</param>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value indicating whether the transport is connected.
    /// </summary>
    bool IsConnected { get; }
}
