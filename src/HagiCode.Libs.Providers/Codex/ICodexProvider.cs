namespace HagiCode.Libs.Providers.Codex;

/// <summary>
/// Defines the SDK-backed Codex provider contract.
/// </summary>
public interface ICodexProvider : ICliProvider
{
    /// <summary>
    /// Creates a Codex thread session for a single execution attempt.
    /// </summary>
    /// <param name="options">The client and thread options.</param>
    /// <param name="cancellationToken">Cancels session creation.</param>
    /// <returns>A disposable session handle that owns the underlying SDK client.</returns>
    Task<CodexSessionHandle> CreateSessionAsync(
        CodexSessionOptions options,
        CancellationToken cancellationToken = default);
}
