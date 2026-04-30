using ManagedCode.CodexSharpSDK.Client;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Owns a single SDK client/thread pair for one Codex execution attempt.
/// </summary>
public sealed class CodexSessionHandle : IAsyncDisposable
{
    private readonly CodexClient _client;
    private int _disposed;

    internal CodexSessionHandle(CodexClient client, CodexThread thread)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
    }

    /// <summary>
    /// Gets the active SDK thread.
    /// </summary>
    public CodexThread Thread { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        Thread.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
