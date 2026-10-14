using System.Text.Json;

namespace HagiCode.Libs.Providers.Omp;

/// <summary>
/// The parts of OMP's <c>--mode rpc-ui</c> protocol (version 1, see <c>omp read omp://rpc.md</c>) the provider speaks: the
/// commands it writes and the frames it recognizes. Every frame is one JSON object on one line.
/// </summary>
internal static class OmpRpcProtocol
{
    public const string SetAskDialogRequestId = "hagicode-set-ask-dialog";
    public const string StateRequestId = "hagicode-get-state";
    public const string PromptRequestId = "hagicode-prompt";
    public const string AbortRequestId = "hagicode-abort";

    public const string ResponseType = "response";
    public const string ExtensionUiRequestType = OmpUserInputRequest.MessageType;
    public const string PromptResultType = "prompt_result";
    public const string SessionSettledType = "session_settled";
    public const string ReadyType = "ready";
    public const string AvailableCommandsUpdateType = "available_commands_update";
    public const string MessageUpdateType = "message_update";

    /// <summary>
    /// The <c>extension_ui_request</c> method OMP sends to close an open dialog itself; <c>targetId</c> names the dialog.
    /// </summary>
    public const string CancelMethod = "cancel";

    public const string CompletedStatus = "completed";
    public const string AbortedStatus = "aborted";
    public const string ErrorStatus = "error";

    /// <summary>
    /// Opts the process in to the structured <c>ask</c> dialog. Off by default; without it the <c>ask</c> tool asks with one
    /// <c>select</c> and an <c>editor</c> per question. A build without the command answers with a failed response.
    /// </summary>
    public static string BuildSetAskDialog() => Serialize(new Dictionary<string, object?>
    {
        ["id"] = SetAskDialogRequestId,
        ["type"] = "set_ask_dialog",
        ["enabled"] = true
    });

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
    /// Answers an <c>ask</c> request: one entry per question, in the order of the request.
    /// </summary>
    public static string BuildAskAnswers(string dialogId, IReadOnlyList<OmpAskAnswer> answers) => Serialize(new Dictionary<string, object?>
    {
        ["type"] = "extension_ui_response",
        ["id"] = dialogId,
        ["answers"] = answers.Select(static answer =>
        {
            var entry = new Dictionary<string, object?>
            {
                ["id"] = answer.Id,
                ["selectedOptions"] = answer.SelectedOptions
            };
            if (answer.CustomInput is not null)
            {
                entry["customInput"] = answer.CustomInput;
            }

            return entry;
        }).ToArray()
    });

    /// <summary>
    /// Dismisses a dialog. For an <c>ask</c> request OMP ends the run as aborted; <c>select</c> and <c>input</c> resolve to
    /// <c>undefined</c>, <c>confirm</c> to <c>false</c>. The <c>timedOut</c> marker is never sent: OMP answers a timed-out
    /// <c>ask</c> with its recommended (or first) options and lets the agent go on, as if the user had chosen them.
    /// </summary>
    public static string BuildDialogCancelled(string dialogId) => Serialize(new Dictionary<string, object?>
    {
        ["type"] = "extension_ui_response",
        ["id"] = dialogId,
        ["cancelled"] = true
    });

    /// <summary>
    /// Whether the dialog <paramref name="method" /> of an extension expects an <c>extension_ui_response</c>. The other
    /// methods (<c>notify</c>, <c>setStatus</c>, <c>setWidget</c>, <c>setTitle</c>, <c>set_editor_text</c>,
    /// <c>open_url</c>) are fire-and-forget.
    /// </summary>
    public static bool ExpectsDialogResponse(string? method)
    {
        return method is "select" or "confirm" or "input" or "editor";
    }

    public static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    public static bool? ReadBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static string Serialize(Dictionary<string, object?> record) => JsonSerializer.Serialize(record);
}
