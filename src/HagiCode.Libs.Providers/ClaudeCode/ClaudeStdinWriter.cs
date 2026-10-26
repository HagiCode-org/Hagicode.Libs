using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Serializes the writes the provider makes to the CLI after it connected.
/// </summary>
/// <remarks>
/// The receive loop (prompt resend, fixed denies) and the background task of every pending question (answers, denies)
/// write concurrently, and <see cref="SubprocessTransport.SendAsync" /> is a plain write plus flush on one stdin writer,
/// so two unsynchronized writers could interleave inside one JSON line. One gate per execution keeps every line whole.
/// </remarks>
internal sealed class ClaudeStdinWriter(ICliTransport transport, Action<CliMessage>? onWrite = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Writes one message, waiting for any write that is already in progress.
    /// </summary>
    /// <remarks>
    /// <paramref name="onWrite" /> sees each message inside the gate, in the order the CLI receives them, before it is sent.
    /// </remarks>
    public async Task SendAsync(CliMessage message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            onWrite?.Invoke(message);
            await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
