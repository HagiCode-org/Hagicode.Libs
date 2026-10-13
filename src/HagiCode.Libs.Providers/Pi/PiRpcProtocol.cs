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
