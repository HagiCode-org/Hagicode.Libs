using System.Text.Json;

namespace HagiCode.Libs.Core.Acp;

/// <summary>
/// Defines the minimal ACP session bootstrap contract used by CLI providers.
/// </summary>
public interface IAcpSessionClient : IAsyncDisposable
{
    /// <summary>
    /// Opens the underlying transport.
    /// </summary>
    /// <param name="cancellationToken">Cancels the connection attempt.</param>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes the ACP protocol.
    /// </summary>
    /// <param name="cancellationToken">Cancels the initialize request.</param>
    /// <returns>The raw initialize result.</returns>
    Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or resumes an ACP session.
    /// </summary>
    /// <param name="workingDirectory">The working directory bound to the session.</param>
    /// <param name="sessionId">An optional session identifier to resume.</param>
    /// <param name="model">An optional model applied after the session is ready.</param>
    /// <param name="cancellationToken">Cancels the bootstrap operation.</param>
    /// <returns>The resulting session handle.</returns>
    Task<AcpSessionHandle> StartSessionAsync(
        string workingDirectory,
        string? sessionId,
        string? model,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the execution mode for an existing ACP session.
    /// </summary>
    /// <param name="sessionId">The target session identifier.</param>
    /// <param name="modeId">The mode identifier to activate.</param>
    /// <param name="cancellationToken">Cancels the mode update request.</param>
    /// <returns>The raw mode update result.</returns>
    Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends one prompt through ACP.
    /// </summary>
    /// <param name="sessionId">The target session identifier.</param>
    /// <param name="prompt">The prompt text.</param>
    /// <param name="cancellationToken">Cancels the prompt request.</param>
    /// <returns>The raw prompt result.</returns>
    Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams inbound ACP notifications.
    /// </summary>
    /// <param name="cancellationToken">Cancels notification enumeration.</param>
    /// <returns>The inbound notifications.</returns>
    IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync(CancellationToken cancellationToken = default);
}
