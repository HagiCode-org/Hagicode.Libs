using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Pi;

/// <summary>
/// One option of an interactive question.
/// </summary>
/// <param name="Label">The option text. It is the value sent back when the option is chosen.</param>
/// <param name="Description">Secondary text that explains the option. Empty when the agent sent none.</param>
public sealed record PiUserQuestionOption(string Label, string Description);

/// <summary>
/// One question of an interactive <c>ask_user_question</c> request.
/// </summary>
/// <param name="Question">The question text. It is also the key of the answer.</param>
/// <param name="Header">A short label for the question. Empty when the agent sent none.</param>
/// <param name="MultiSelect">Whether the user may pick more than one option.</param>
/// <param name="Options">The offered options.</param>
public sealed record PiUserQuestion(
    string Question,
    string Header,
    bool MultiSelect,
    IReadOnlyList<PiUserQuestionOption> Options);

/// <summary>
/// An interactive question the Pi agent asks through the bundled <c>ask_user_question</c> extension tool.
/// </summary>
/// <param name="RequestId">The Pi <c>extension_ui_request</c> id the answer is written back for. Private to the provider conversation.</param>
/// <param name="ToolCallId">The id of the agent's tool call that asked the question. It is stable within a Pi session.</param>
/// <param name="Questions">The questions in the order the agent asked them.</param>
public sealed record PiUserInputRequest(
    string RequestId,
    string ToolCallId,
    IReadOnlyList<PiUserQuestion> Questions)
{
    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the CLI message that carries a request. The provider yields an interactive
    /// request to its caller as this message type, in the position it arrived in the stream.
    /// </summary>
    public const string MessageType = "extension_ui_request";

    /// <summary>
    /// The name of the tool that asks interactive questions.
    /// </summary>
    public const string QuestionToolName = "ask_user_question";

    /// <summary>
    /// The version of the dialog contract between the bundled extension and the provider.
    /// </summary>
    public const int WireVersion = 1;

    /// <summary>
    /// The first line of the title of the <c>input</c> dialog that carries a question request. The rest of the title, after
    /// a newline, is the JSON <c>{ "toolCallId": "...", "questions": [ ... ] }</c>. A dialog that carries another version
    /// is not a question request for this provider.
    /// </summary>
    public const string WireMarker = "hagicode:ask_user_question:v1";

    /// <summary>
    /// Reads an interactive question request from an <c>extension_ui_request</c> stream message.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when the message is not an <c>input</c> dialog that carries <see cref="WireMarker" /> or its
    /// content is not valid.
    /// </returns>
    public static bool TryFromCliMessage(CliMessage message, out PiUserInputRequest request)
    {
        request = null!;
        ArgumentNullException.ThrowIfNull(message);

        var content = message.Content;
        if (message.Type != MessageType
            || content.ValueKind != JsonValueKind.Object
            || ReadString(content, "method") != "input"
            || ReadString(content, "id") is not { Length: > 0 } requestId
            || ReadString(content, "title") is not { } title)
        {
            return false;
        }

        return TryParseDialogTitle(requestId, title, out request);
    }

    /// <summary>
    /// Reads the request out of an <c>input</c> dialog title. The title is the marker line, a newline, then the JSON payload.
    /// </summary>
    internal static bool TryParseDialogTitle(string requestId, string title, out PiUserInputRequest request)
    {
        request = null!;

        var newline = title.IndexOf('\n');
        if (newline < 0 || title.AsSpan(0, newline).Trim().CompareTo(WireMarker, StringComparison.Ordinal) != 0)
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(title.AsMemory(newline + 1));
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("questions", out var questionElements)
                || questionElements.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var questions = new List<PiUserQuestion>();
            foreach (var questionElement in questionElements.EnumerateArray())
            {
                if (ReadString(questionElement, "question") is not { Length: > 0 } text)
                {
                    return false;
                }

                var options = new List<PiUserQuestionOption>();
                if (questionElement.TryGetProperty("options", out var optionElements)
                    && optionElements.ValueKind == JsonValueKind.Array)
                {
                    foreach (var optionElement in optionElements.EnumerateArray())
                    {
                        if (ReadString(optionElement, "label") is not { } label)
                        {
                            return false;
                        }

                        options.Add(new PiUserQuestionOption(label, ReadString(optionElement, "description") ?? string.Empty));
                    }
                }

                questions.Add(new PiUserQuestion(
                    text,
                    ReadString(questionElement, "header") ?? string.Empty,
                    questionElement.TryGetProperty("multiSelect", out var multiSelect) && multiSelect.ValueKind == JsonValueKind.True,
                    options));
            }

            var toolCallId = ReadString(root, "toolCallId") is { Length: > 0 } id ? id : requestId;
            request = new PiUserInputRequest(requestId, toolCallId, questions);
            return true;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

/// <summary>
/// The answers a <see cref="PiOptions.UserInputHandler" /> returns for one request.
/// </summary>
/// <param name="Answers">
/// For each question text, the selected option labels or the free text the user typed. A value that is not one of the
/// offered labels is sent unchanged. A multi-select question is sent as one string with the values joined by a comma and
/// a space.
/// </param>
public sealed record PiUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers)
{
    /// <summary>
    /// Serializes the answers into the <c>value</c> of the <c>extension_ui_response</c> that answers the question dialog:
    /// the JSON <c>{ "answers": { "&lt;question text&gt;": "&lt;answer&gt;" } }</c>, where every answer is one string with its
    /// values joined by a comma and a space.
    /// </summary>
    internal string ToDialogValue()
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in Answers)
        {
            answers[pair.Key] = string.Join(", ", pair.Value);
        }

        return JsonSerializer.Serialize(new Dictionary<string, object> { ["answers"] = answers });
    }
}
