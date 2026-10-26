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
    public const string SetInterruptModeRequestId = "hagicode-set-interrupt-mode";

    /// <summary>The id prefix of every <c>steer</c> command the provider writes; the reply carries the same id.</summary>
    public const string SteerRequestIdPrefix = "hagicode-steer-";

    public const string ResponseType = "response";
    public const string ExtensionUiRequestType = OmpUserInputRequest.MessageType;
    public const string PromptResultType = "prompt_result";
    public const string SessionSettledType = "session_settled";
    public const string ReadyType = "ready";
    public const string AvailableCommandsUpdateType = "available_commands_update";
    public const string MessageUpdateType = "message_update";
    public const string MessageStartType = "message_start";
    public const string AgentStartType = "agent_start";
    public const string AgentEndType = "agent_end";

    public const string SteerCommand = "steer";
    public const string SetInterruptModeCommand = "set_interrupt_mode";

    /// <summary>
    /// The interrupt mode that keeps a running tool call's real result when a steer arrives. The default, <c>immediate</c>,
    /// backgrounds the running tool and replaces its result with a placeholder.
    /// </summary>
    public const string InterruptModeWait = "wait";

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
    /// Sets the interrupt mode to <c>wait</c>, so a steer does not background a running tool call.
    /// </summary>
    public static string BuildSetInterruptMode() => Serialize(new Dictionary<string, object?>
    {
        ["id"] = SetInterruptModeRequestId,
        ["type"] = SetInterruptModeCommand,
        ["mode"] = InterruptModeWait
    });

    /// <summary>
    /// Writes one steer. OMP takes the message into the run after the current turn or tool call.
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
    public static bool TryReadSteerReply(JsonElement frame, out OmpSteerReply reply)
    {
        reply = default;
        if (ReadString(frame, "type") != ResponseType
            || ReadString(frame, "command") != SteerCommand
            || ReadString(frame, "id") is not { Length: > 0 } requestId
            || !requestId.StartsWith(SteerRequestIdPrefix, StringComparison.Ordinal)
            || !frame.TryGetProperty("success", out var success)
            || success.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        reply = new OmpSteerReply(requestId, success.ValueKind == JsonValueKind.True);
        return true;
    }

    /// <summary>
    /// Reads the text of a steering <c>message_start</c>: the single text block of a user message that OMP flags as
    /// <c>steering</c>, the record it writes when it takes a steer into the run. It returns <see langword="null" /> for any
    /// other frame, including the run's own prompt echo (not flagged) and a user message with more than one block.
    /// </summary>
    /// <remarks>
    /// Only <c>message_start</c> counts. OMP repeats the same message in <c>message_end</c>, and counting that too would consume
    /// the next steer that has the same text.
    /// </remarks>
    public static string? ReadSteeringUserMessageText(JsonElement frame)
    {
        if (ReadString(frame, "type") != MessageStartType
            || !frame.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || ReadString(message, "role") != "user"
            || ReadBoolean(message, "steering") != true
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array
            || content.GetArrayLength() != 1)
        {
            return null;
        }

        var block = content[0];
        return ReadString(block, "type") == "text" ? ReadString(block, "text") : null;
    }

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

/// <summary>
/// OMP's answer to one <c>steer</c> command: the request id the provider wrote and whether OMP accepted the message.
/// </summary>
internal readonly record struct OmpSteerReply(string RequestId, bool Success);
