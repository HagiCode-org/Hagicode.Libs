using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.ClaudeCode;

/// <summary>
/// One option of an interactive question.
/// </summary>
/// <param name="Label">The option text. It is the value sent back when the option is chosen.</param>
/// <param name="Description">Secondary text that explains the option. Empty when the CLI sent none.</param>
public sealed record ClaudeUserQuestionOption(string Label, string Description);

/// <summary>
/// One question of an interactive <c>AskUserQuestion</c> request.
/// </summary>
/// <param name="Question">The question text. It is also the key of the answer.</param>
/// <param name="Header">A short label for the question. Empty when the CLI sent none.</param>
/// <param name="MultiSelect">Whether the user may pick more than one option.</param>
/// <param name="Options">The offered options.</param>
public sealed record ClaudeUserQuestion(
    string Question,
    string Header,
    bool MultiSelect,
    IReadOnlyList<ClaudeUserQuestionOption> Options);

/// <summary>
/// An interactive question the Claude CLI asks through its <c>AskUserQuestion</c> tool.
/// </summary>
/// <param name="RequestId">The CLI request id the answer is written back for. Private to the provider conversation.</param>
/// <param name="ToolUseId">The id of the <c>tool_use</c> block that asked the question. It is stable within a CLI session.</param>
/// <param name="ToolName">The tool that asked, <c>AskUserQuestion</c>.</param>
/// <param name="Questions">The questions in the order the agent asked them.</param>
/// <param name="Input">The original tool input. The answer response echoes it with an added <c>answers</c> member.</param>
public sealed record ClaudeUserInputRequest(
    string RequestId,
    string ToolUseId,
    string ToolName,
    IReadOnlyList<ClaudeUserQuestion> Questions,
    JsonElement Input)
{
    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the CLI message that carries a request. The provider yields an interactive
    /// request to its caller as this message type, in the position it arrived in the stream.
    /// </summary>
    public const string MessageType = "control_request";

    /// <summary>
    /// The name of the tool that asks interactive questions.
    /// </summary>
    public const string AskUserQuestionToolName = "AskUserQuestion";

    /// <summary>
    /// Reads an interactive question request from a <c>control_request</c> stream message.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when the message is not a <c>can_use_tool</c> request for <c>AskUserQuestion</c> or its
    /// content is not valid.
    /// </returns>
    public static bool TryFromCliMessage(CliMessage message, out ClaudeUserInputRequest request)
    {
        request = null!;
        if (!ClaudeControlProtocol.TryParseRequest(message, out var control)
            || !control.IsUserQuestion
            || control.ToolUseId is null)
        {
            return false;
        }

        var questions = new List<ClaudeUserQuestion>();
        if (control.Input.ValueKind == JsonValueKind.Object
            && control.Input.TryGetProperty("questions", out var questionElements)
            && questionElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var questionElement in questionElements.EnumerateArray())
            {
                if (ReadString(questionElement, "question") is not { Length: > 0 } text)
                {
                    return false;
                }

                var options = new List<ClaudeUserQuestionOption>();
                if (questionElement.TryGetProperty("options", out var optionElements)
                    && optionElements.ValueKind == JsonValueKind.Array)
                {
                    foreach (var optionElement in optionElements.EnumerateArray())
                    {
                        if (ReadString(optionElement, "label") is not { } label)
                        {
                            return false;
                        }

                        options.Add(new ClaudeUserQuestionOption(label, ReadString(optionElement, "description") ?? string.Empty));
                    }
                }

                questions.Add(new ClaudeUserQuestion(
                    text,
                    ReadString(questionElement, "header") ?? string.Empty,
                    questionElement.TryGetProperty("multiSelect", out var multiSelect) && multiSelect.ValueKind == JsonValueKind.True,
                    options));
            }
        }

        var input = control.Input.ValueKind == JsonValueKind.Undefined
            ? JsonSerializer.SerializeToElement(new { })
            : control.Input.Clone();
        request = new ClaudeUserInputRequest(control.RequestId, control.ToolUseId, control.ToolName, questions, input);
        return true;
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
/// The answers a <see cref="ClaudeCodeOptions.UserInputHandler" /> returns for one request.
/// </summary>
/// <param name="Answers">
/// For each question text, the selected option labels or the free text the user typed. A value that is not one of the
/// offered labels is sent unchanged. A multi-select question is sent as one string with the values joined by a comma and
/// a space.
/// </param>
public sealed record ClaudeUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers);
