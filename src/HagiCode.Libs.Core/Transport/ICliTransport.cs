namespace HagiCode.Libs.Core.Transport;

/// <summary>
/// Defines the lifecycle for communicating with a CLI subprocess.
/// </summary>
public interface ICliTransport : IAsyncDisposable
{
    /// <summary>
    /// Opens the underlying connection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the CLI subprocess.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancels the send operation.</param>
    Task SendAsync(CliMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams messages from the CLI subprocess.
    /// </summary>
    /// <param name="cancellationToken">Cancels the receive loop.</param>
    /// <returns>An async sequence of messages.</returns>
    IAsyncEnumerable<CliMessage> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to interrupt the running subprocess.
    /// </summary>
    /// <param name="cancellationToken">Cancels the interrupt request.</param>
    Task InterruptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the underlying connection.
    /// </summary>
    /// <param name="cancellationToken">Cancels the disconnect operation.</param>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value indicating whether the transport is currently connected.
    /// </summary>
    bool IsConnected { get; }
}
