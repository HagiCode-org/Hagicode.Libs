using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// Decides which messages reach the caller while a prompt written to the CLI has not started its own turn.
/// </summary>
/// <remarks>
/// On resume the CLI runs an injected turn for a stopped background-task notification before the host prompt's turn.
/// That turn is a <c>system/init</c> followed by an empty <c>result</c> with no conversation, so it is dropped instead of
/// being shown as an answer. Turns are held until the prompt's replay shows whether they are the prompt's own. A held
/// turn that produces output or an error is released, and so is a held turn still open when the stream ends, so a CLI
/// that never replays the prompt still shows what it wrote.
/// </remarks>
internal sealed class ClaudeTurnDelivery
{
    private readonly List<CliMessage> _heldMessages = [];
    private bool _turnOpen;
    private bool _turnHasConversation;

    /// <summary>
    /// Returns the messages to deliver for one observed message, in delivery order.
    /// </summary>
    /// <param name="message">The message that was just received.</param>
    /// <param name="hostPromptAwaitingStart">Whether a prompt written to the CLI has not been replayed yet.</param>
    /// <param name="steerEcho">
    /// Whether the message is the echo of a steer. The echo is dropped, because the backend records the steer itself, but it
    /// does not start or finish the prompt's turn, so held messages stay held.
    /// </param>
    public IReadOnlyList<CliMessage> Accept(CliMessage message, bool hostPromptAwaitingStart, bool steerEcho = false)
    {
        if (steerEcho)
        {
            return [];
        }

        if (ClaudeStreamCompletionTracker.IsPromptReplay(message))
        {
            return Release();
        }

        if (!hostPromptAwaitingStart)
        {
            return [.. Release(), message];
        }

        if (!_turnOpen)
        {
            if (!IsTurnStart(message))
            {
                return [message];
            }

            _turnOpen = true;
        }

        _heldMessages.Add(message);
        if (IsConversation(message))
        {
            _turnHasConversation = true;
        }

        if (IsResult(message) && IsInjectedEmptyResult(message))
        {
            Reset();
            return [];
        }

        if (IsResult(message) || IsError(message))
        {
            return Release();
        }

        return [];
    }

    /// <summary>
    /// Returns the held messages when the run ends before their turn did.
    /// </summary>
    public IReadOnlyList<CliMessage> Drain()
    {
        return Release();
    }

    private bool IsInjectedEmptyResult(CliMessage message)
    {
        return !_turnHasConversation
               && !ClaudeStreamCompletionTracker.IsErrorResult(message.Content)
               && IsEmptyResult(message.Content);
    }

    private IReadOnlyList<CliMessage> Release()
    {
        var released = _heldMessages.ToArray();
        Reset();
        return released;
    }

    private void Reset()
    {
        _heldMessages.Clear();
        _turnOpen = false;
        _turnHasConversation = false;
    }

    private static bool IsTurnStart(CliMessage message)
    {
        return string.Equals(message.Type, "system", StringComparison.OrdinalIgnoreCase)
               && message.Content.ValueKind == JsonValueKind.Object
               && message.Content.TryGetProperty("subtype", out var subtype)
               && subtype.ValueKind == JsonValueKind.String
               && string.Equals(subtype.GetString(), "init", StringComparison.Ordinal);
    }

    private static bool IsConversation(CliMessage message)
    {
        return string.Equals(message.Type, "assistant", StringComparison.OrdinalIgnoreCase)
               || string.Equals(message.Type, "user", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsResult(CliMessage message)
    {
        return string.Equals(message.Type, "result", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsError(CliMessage message)
    {
        return string.Equals(message.Type, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmptyResult(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("result", out var result))
        {
            return true;
        }

        return result.ValueKind == JsonValueKind.Null
               || (result.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(result.GetString()));
    }
}
