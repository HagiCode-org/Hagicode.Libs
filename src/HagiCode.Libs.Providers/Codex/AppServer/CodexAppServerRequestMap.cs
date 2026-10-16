using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// Tracks the server requests that codex app-server has raised and not yet been answered, keyed by request id.
/// </summary>
/// <remarks>
/// A server request is answered exactly once. <see cref="TryBeginResponse"/> hands the original JSON-RPC id to the first caller
/// and removes the entry, so a second answer, an expiry after an answer, or an interrupt after an answer does nothing.
/// </remarks>
internal sealed class CodexAppServerRequestMap
{
    private readonly ConcurrentDictionary<string, PendingServerRequest> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Registers an open server request.
    /// </summary>
    /// <param name="key">The request id as text.</param>
    /// <param name="rawId">The original JSON-RPC id, which must be echoed back unchanged.</param>
    /// <returns><see langword="false"/> when a request with the same id is already open; the duplicate is ignored.</returns>
    public bool TryRegister(string key, JsonNode? rawId)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return _pending.TryAdd(key, new PendingServerRequest(rawId));
    }

    /// <summary>
    /// Takes the right to answer an open request. The first caller receives the original id; later callers receive
    /// <see langword="false"/>.
    /// </summary>
    public bool TryBeginResponse(string key, out JsonNode? rawId)
    {
        rawId = null;
        if (!_pending.TryRemove(key, out var pending))
        {
            return false;
        }

        rawId = pending.RawId;
        return true;
    }

    /// <summary>
    /// Whether a request with this id is still open and unanswered.
    /// </summary>
    public bool IsOpen(string key) => _pending.ContainsKey(key);

    /// <summary>
    /// The ids of the open requests, for logging and for the run's cleanup.
    /// </summary>
    public IReadOnlyList<string> OpenKeys() => _pending.Keys.ToArray();

    private sealed record PendingServerRequest(JsonNode? RawId);
}
