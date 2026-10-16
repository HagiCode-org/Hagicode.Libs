using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Client;

namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Owns a single SDK client/thread pair for one Codex execution attempt. On the interactive transport it also owns the
/// codex app-server session that carries the thread.
/// </summary>
public sealed class CodexSessionHandle : IAsyncDisposable
{
    private readonly CodexClient _client;
    private readonly CodexAppServerSession? _appServer;
    private int _disposed;

    internal CodexSessionHandle(CodexClient client, CodexThread thread, CodexAppServerSession? appServer = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Thread = thread ?? throw new ArgumentNullException(nameof(thread));
        _appServer = appServer;
    }

    /// <summary>
    /// Gets the active SDK thread.
    /// </summary>
    public CodexThread Thread { get; }

    /// <summary>
    /// Gets whether this session runs on the interactive transport, so it can deliver user answers. It is <see langword="false"/>
    /// for the exec transport, where no question can be asked.
    /// </summary>
    public bool SupportsUserQuestions => _appServer is not null;

    /// <summary>
    /// Answers an open question of the active turn by its request id. Passing <see langword="null"/> declines it. Only the
    /// first answer for a request id takes effect.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call answered the request; <see langword="false"/> when the request is unknown, already
    /// answered, or the session is on the exec transport.
    /// </returns>
    public Task<bool> TryAnswerUserQuestionAsync(
        string requestId,
        CodexUserQuestionResponse? response,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        return _appServer is null
            ? Task.FromResult(false)
            : _appServer.RespondToUserQuestionAsync(requestId, response, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        Thread.Dispose();
        _client.Dispose();
        if (_appServer is not null)
        {
            await _appServer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
