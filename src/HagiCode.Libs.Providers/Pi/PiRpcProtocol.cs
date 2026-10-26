using System.Text.Json;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// The parts of Pi's <c>--mode rpc</c> protocol the provider speaks: the commands it writes and the records it recognizes.
/// Every record is one JSON object on one line.
/// </summary>
internal static class PiRpcProtocol
{
    public const string StateRequestId = "hagicode-get-state";
    public const string PromptRequestId = "hagicode-prompt";
    public const string AbortRequestId = "hagicode-abort";

    /// <summary>
    /// The id prefix of the <c>steer</c> commands the provider writes. Pi echoes only the command id, so the provider maps each id
    /// back to its steer.
    /// </summary>
    public const string SteerRequestIdPrefix = "hagicode-steer-";

    public const string SteerCommand = "steer";
    public const string MessageStartType = "message_start";
    public const string AgentEndType = "agent_end";
    public const string DispositionQueued = "queued";
    public const string DispositionHandled = "handled";

    public const string ResponseType = "response";
    public const string AgentSettledType = "agent_settled";
    public const string ExtensionUiRequestType = PiUserInputRequest.MessageType;

    /// <summary>
    /// The text Pi 1.x writes to standard error and exits with code 1 when two extensions register the same tool name:
    /// <c>Tool "ask_user_question" conflicts with &lt;path&gt;</c>. It happens during startup, before any protocol record.
    /// </summary>
    public static readonly string QuestionToolConflictText =
        $"Tool \"{PiUserInputRequest.QuestionToolName}\" conflicts with";

    public static string BuildGetState() => Serialize(new Dictionary<string, object?>
    {
        ["id"] = StateRequestId,
        ["type"] = "get_state"
    });

    public static string BuildPrompt(string message) => Serialize(new Dictionary<string, object?>
    {
        ["id"] = PromptRequestId,
        ["type"] = "prompt",
        ["message"] = message
    });

    public static string BuildAbort() => Serialize(new Dictionary<string, object?>
    {
        ["id"] = AbortRequestId,
        ["type"] = "abort"
    });

    /// <summary>
    /// Asks Pi to deliver a follow-up message after the current turn's tool calls. Pi answers with a <c>steer</c> response
    /// whose disposition is <c>queued</c> or <c>handled</c>, or with success false.
    /// </summary>
    public static string BuildSteer(string requestId, string message) => Serialize(new Dictionary<string, object?>
    {
        ["id"] = requestId,
        ["type"] = SteerCommand,
        ["message"] = message
    });

    /// <summary>
    /// Reads the answer to a <c>steer</c> command the provider wrote: a response to the command with a
    /// <c>hagicode-steer-</c> id. Anything else, including a response to another command, is not a steer reply.
    /// </summary>
    public static bool TryReadSteerReply(JsonElement record, out SteerReply reply)
    {
        reply = default;
        if (ReadString(record, "type") != ResponseType
            || ReadString(record, "command") != SteerCommand
            || ReadString(record, "id") is not { Length: > 0 } requestId
            || !requestId.StartsWith(SteerRequestIdPrefix, StringComparison.Ordinal)
            || !record.TryGetProperty("success", out var success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        var disposition = record.TryGetProperty("data", out var data) ? ReadString(data, "disposition") : null;
        reply = new SteerReply(requestId, success.ValueKind == JsonValueKind.True, disposition);
        return true;
    }

    /// <summary>
    /// Reads the text of a user <c>message_start</c>: the text of its single text block, or a plain string content. This is the
    /// record Pi writes when it injects a steer into the run. It returns <see langword="null" /> for any other record, for an
    /// assistant message, and for a user message with more than one block.
    /// </summary>
    public static string? ReadUserMessageText(JsonElement record)
    {
        if (ReadString(record, "type") != MessageStartType
            || !record.TryGetProperty("message", out var message)
            || ReadString(message, "role") != "user"
            || !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array || content.GetArrayLength() != 1)
        {
            return null;
        }

        var block = content[0];
        return ReadString(block, "type") == "text" ? ReadString(block, "text") : null;
    }

    /// <summary>
    /// Answers a dialog with a value (the <c>input</c> of a question).
    /// </summary>
    public static string BuildDialogValue(string dialogId, string value) => Serialize(new Dictionary<string, object?>
    {
        ["type"] = "extension_ui_response",
        ["id"] = dialogId,
        ["value"] = value
    });

    /// <summary>
    /// Dismisses a dialog. Pi resolves it as <c>undefined</c> (<c>false</c> for a confirmation).
    /// </summary>
    public static string BuildDialogCancelled(string dialogId) => Serialize(new Dictionary<string, object?>
    {
        ["type"] = "extension_ui_response",
        ["id"] = dialogId,
        ["cancelled"] = true
    });

    /// <summary>
    /// Whether the dialog <paramref name="method" /> expects an <c>extension_ui_response</c>. The other methods
    /// (<c>notify</c>, <c>setStatus</c>, <c>setWidget</c>, <c>setTitle</c>, <c>set_editor_text</c>) are fire-and-forget.
    /// </summary>
    public static bool ExpectsDialogResponse(string? method)
    {
        return method is "select" or "confirm" or "input" or "editor";
    }

    /// <summary>
    /// Whether standard error says that Pi refused to start because a user extension registers the question tool too.
    /// </summary>
    public static bool IsQuestionToolConflict(string? standardError)
    {
        return standardError is not null
               && standardError.Contains(QuestionToolConflictText, StringComparison.Ordinal);
    }

    public static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string Serialize(Dictionary<string, object?> record) => JsonSerializer.Serialize(record);
}

/// <summary>
/// The answer to one <c>steer</c> command: the request id it answers, whether Pi accepted it, and the disposition
/// (<c>queued</c> or <c>handled</c>) when it did.
/// </summary>
internal readonly record struct SteerReply(string RequestId, bool Success, string? Disposition);
