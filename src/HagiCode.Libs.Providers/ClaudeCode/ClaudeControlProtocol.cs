using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// The Claude CLI control protocol of <c>--permission-prompt-tool stdio</c>: the <c>control_request</c> messages the CLI
/// writes when it needs a decision from its host, and the <c>control_response</c> messages the host writes back.
/// </summary>
/// <remarks>
/// The protocol is undocumented and version-sensitive, so every wire shape of it lives here. Verified against CLI 2.1.159
/// and 2.1.292 (see <c>ClaudeUserQuestionCapture</c>). A request is
/// <c>{"type":"control_request","request_id":…,"request":{"subtype":"can_use_tool","tool_name":…,"input":{…},"tool_use_id":…}}</c>
/// and the answer is
/// <c>{"type":"control_response","response":{"subtype":"success","request_id":…,"response":{"behavior":"allow"|"deny",…}}}</c>.
/// </remarks>
internal static class ClaudeControlProtocol
{
    internal const string ControlResponseType = "control_response";
    internal const string CanUseToolSubtype = "can_use_tool";

    /// <summary>
    /// The message the host writes when it denies a tool call it does not ask the user about. The CLI turns it into the
    /// tool result the model sees.
    /// </summary>
    internal const string FixedDenyMessage = "This tool call needs a permission that cannot be granted in this session.";

    /// <summary>
    /// The message the host writes when an interactive question cannot be put to a user.
    /// </summary>
    internal const string QuestionUnavailableMessage = "The user cannot be asked a question in this session.";

    /// <summary>
    /// The message the host writes when no answer arrives before the question timeout.
    /// </summary>
    internal const string QuestionTimedOutMessage = "No answer was received from the user in time.";

    /// <summary>
    /// The message the host writes when the user gave no answer.
    /// </summary>
    internal const string NoAnswerMessage = "The user gave no answer.";

    /// <summary>
    /// The message the host writes, best effort, when the run is cancelled while a question is pending.
    /// </summary>
    internal const string QuestionCancelledMessage = "The run was cancelled before the user answered.";

    /// <summary>
    /// The fields of a <c>control_request</c> the provider needs.
    /// </summary>
    /// <param name="RequestId">The CLI request id the response must repeat.</param>
    /// <param name="Subtype">The request subtype, for example <c>can_use_tool</c>.</param>
    /// <param name="ToolName">The tool the request is about; empty when absent.</param>
    /// <param name="ToolUseId">The id of the <c>tool_use</c> block the request is about, when present.</param>
    /// <param name="Input">The tool input; an undefined element when absent.</param>
    /// <param name="RequiresUserInteraction">
    /// Whether the CLI marked the request as one that needs a human. CLI 2.1.292 sets it for <c>AskUserQuestion</c>;
    /// CLI 2.1.159 does not, so it is never the only signal that a request is a question.
    /// </param>
    internal sealed record ControlRequest(
        string RequestId,
        string Subtype,
        string ToolName,
        string? ToolUseId,
        JsonElement Input,
        bool RequiresUserInteraction)
    {
        /// <summary>
        /// Gets a value indicating whether this is a permission request for the interactive question tool. The tool name
        /// decides, not <see cref="RequiresUserInteraction" />, because CLI 2.1.159 sends the request without that member.
        /// </summary>
        public bool IsUserQuestion =>
            string.Equals(Subtype, CanUseToolSubtype, StringComparison.Ordinal)
            && string.Equals(ToolName, ClaudeUserInputRequest.AskUserQuestionToolName, StringComparison.Ordinal);

        /// <summary>
        /// Gets a value indicating whether this is a tool permission request.
        /// </summary>
        public bool IsToolPermission => string.Equals(Subtype, CanUseToolSubtype, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a <c>control_request</c> stream message.
    /// </summary>
    /// <returns><see langword="false" /> when the message is not a well-formed control request.</returns>
    internal static bool TryParseRequest(CliMessage message, out ControlRequest request)
    {
        request = null!;
        if (!string.Equals(message.Type, ClaudeUserInputRequest.MessageType, StringComparison.Ordinal)
            || message.Content.ValueKind != JsonValueKind.Object
            || GetString(message.Content, "request_id") is not { } requestId
            || !message.Content.TryGetProperty("request", out var body)
            || body.ValueKind != JsonValueKind.Object
            || GetString(body, "subtype") is not { } subtype)
        {
            return false;
        }

        var input = body.TryGetProperty("input", out var inputElement) ? inputElement : default;
        request = new ControlRequest(
            requestId,
            subtype,
            GetString(body, "tool_name") ?? string.Empty,
            GetString(body, "tool_use_id"),
            input,
            body.TryGetProperty("requires_user_interaction", out var interaction) && interaction.ValueKind == JsonValueKind.True);
        return true;
    }

    /// <summary>
    /// Reports whether the message is the CLI echoing a <c>control_response</c> the host wrote. The CLI replays everything
    /// the host writes because of <c>--replay-user-messages</c>; the echo arrives as a top-level
    /// <c>control_response</c> message, not as a replayed <c>user</c> message. The host never sends a request that the CLI
    /// answers with a <c>control_response</c> of its own, so every such message is an echo.
    /// </summary>
    internal static bool IsControlResponseEcho(CliMessage message)
    {
        return string.Equals(message.Type, ControlResponseType, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds the response that lets a question go ahead with the user's answers.
    /// </summary>
    /// <param name="requestId">The request id of the question.</param>
    /// <param name="originalInput">The tool input of the request.</param>
    /// <param name="answers">The answers by question text.</param>
    internal static CliMessage BuildAllowResponse(
        string requestId,
        JsonElement originalInput,
        IReadOnlyDictionary<string, IReadOnlyList<string>> answers)
    {
        // The answer text is user input that ends up on the stdin of the CLI, so it is serialized, never concatenated.
        var updatedInput = originalInput.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(originalInput.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        var answerObject = new JsonObject();
        foreach (var answer in answers)
        {
            answerObject[answer.Key] = JoinAnswer(answer.Value);
        }

        updatedInput["answers"] = answerObject;
        return BuildResponse(requestId, new JsonObject
        {
            ["behavior"] = "allow",
            ["updatedInput"] = updatedInput
        });
    }

    /// <summary>
    /// Builds the response that refuses a tool call. The CLI reports the call as denied and gives the model the message.
    /// </summary>
    internal static CliMessage BuildDenyResponse(string requestId, string message)
    {
        return BuildResponse(requestId, new JsonObject
        {
            ["behavior"] = "deny",
            ["message"] = message
        });
    }

    /// <summary>
    /// Joins the selected values of one question into the single string the CLI expects: a comma and a space between
    /// values, each value unchanged.
    /// </summary>
    internal static string JoinAnswer(IReadOnlyList<string> values)
    {
        return string.Join(", ", values);
    }

    private static CliMessage BuildResponse(string requestId, JsonObject decision)
    {
        var root = new JsonObject
        {
            ["type"] = ControlResponseType,
            ["response"] = new JsonObject
            {
                ["subtype"] = "success",
                ["request_id"] = requestId,
                ["response"] = decision
            }
        };

        return new CliMessage(ControlResponseType, JsonSerializer.SerializeToElement(root));
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
               && property.GetString() is { Length: > 0 } value
            ? value
            : null;
    }
}
