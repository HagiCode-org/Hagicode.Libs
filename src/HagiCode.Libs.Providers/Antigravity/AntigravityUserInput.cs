using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Antigravity;

/// <summary>One option of an Antigravity question.</summary>
/// <param name="Label">The option text. It is the value sent back when the option is chosen.</param>
/// <param name="Description">Secondary text. Empty when the agent sent none.</param>
public sealed record AntigravityUserQuestionOption(string Label, string Description);

/// <summary>One question of an Antigravity <c>ask_question</c> tool call.</summary>
/// <param name="Question">The question text. It is also the key of the answer.</param>
/// <param name="Header">A short label. Empty, because the CLI does not send one.</param>
/// <param name="MultiSelect">Whether the user may pick more than one option.</param>
/// <param name="Options">The offered options.</param>
public sealed record AntigravityUserQuestion(
    string Question,
    string Header,
    bool MultiSelect,
    IReadOnlyList<AntigravityUserQuestionOption> Options);

/// <summary>
/// An interactive question the Antigravity agent asks through its <c>ask_question</c> tool, as seen in an ACP <c>tool_call</c>.
/// </summary>
/// <remarks>
/// Antigravity does not wait for the answer. The question ends the turn, and the answer goes out as the next prompt on the same
/// session. The provider therefore calls <see cref="AntigravityOptions.UserInputHandler"/> once the turn that asked has ended.
/// A question that appears only as agent text is not a request for this type.
/// </remarks>
/// <param name="ToolCallId">The id of the tool call that asked the questions. It identifies the question within a session.</param>
/// <param name="Questions">The questions in the order the agent asked them.</param>
public sealed record AntigravityUserInputRequest(string ToolCallId, IReadOnlyList<AntigravityUserQuestion> Questions)
{
    /// <summary>The <see cref="CliMessage.Type" /> of the CLI message that carries a question request.</summary>
    public const string MessageType = "user.question";

    /// <summary>The ACP tool name the Antigravity agent uses to ask a question.</summary>
    public const string QuestionToolName = "ask_question";

    /// <summary>
    /// Reads a question request from a <see cref="MessageType"/> message.
    /// </summary>
    /// <returns><see langword="false" /> when the message is not a question request or has no usable question.</returns>
    public static bool TryFromCliMessage(CliMessage message, out AntigravityUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(message);
        request = null!;

        var content = message.Content;
        if (!string.Equals(message.Type, MessageType, StringComparison.Ordinal) ||
            content.ValueKind != JsonValueKind.Object ||
            ReadString(content, "tool_call_id") is not { Length: > 0 } toolCallId ||
            !content.TryGetProperty("questions", out var questionsElement) ||
            questionsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var questions = new List<AntigravityUserQuestion>();
        foreach (var questionElement in questionsElement.EnumerateArray())
        {
            if (questionElement.ValueKind != JsonValueKind.Object ||
                ReadString(questionElement, "question") is not { Length: > 0 } questionText)
            {
                continue;
            }

            questions.Add(new AntigravityUserQuestion(
                questionText,
                ReadString(questionElement, "header") ?? string.Empty,
                questionElement.TryGetProperty("multi_select", out var multiSelect) && multiSelect.ValueKind == JsonValueKind.True,
                ReadOptions(questionElement)));
        }

        if (questions.Count == 0)
        {
            return false;
        }

        request = new AntigravityUserInputRequest(toolCallId, questions);
        return true;
    }

    private static IReadOnlyList<AntigravityUserQuestionOption> ReadOptions(JsonElement questionElement)
    {
        var options = new List<AntigravityUserQuestionOption>();
        if (!questionElement.TryGetProperty("options", out var optionsElement) ||
            optionsElement.ValueKind != JsonValueKind.Array)
        {
            return options;
        }

        foreach (var optionElement in optionsElement.EnumerateArray())
        {
            // The CLI sends options as plain strings; an object with label and description is accepted too.
            if (optionElement.ValueKind == JsonValueKind.String)
            {
                var label = optionElement.GetString();
                if (!string.IsNullOrWhiteSpace(label))
                {
                    options.Add(new AntigravityUserQuestionOption(label, string.Empty));
                }
            }
            else if (optionElement.ValueKind == JsonValueKind.Object &&
                     ReadString(optionElement, "label") is { Length: > 0 } objectLabel)
            {
                options.Add(new AntigravityUserQuestionOption(objectLabel, ReadString(optionElement, "description") ?? string.Empty));
            }
        }

        return options;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

/// <summary>The answers a <see cref="AntigravityOptions.UserInputHandler"/> returns for one <see cref="AntigravityUserInputRequest"/>.</summary>
/// <param name="Answers">For each question text, the selected option labels or the free text the user typed.</param>
public sealed record AntigravityUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers)
{
    /// <summary>
    /// Builds the prompt that carries the answers back to the agent as the next turn of the same session.
    /// </summary>
    public static string BuildAnswerPrompt(AntigravityUserInputRequest request, AntigravityUserInputResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var builder = new StringBuilder("Here are my answers to your questions:");
        foreach (var question in request.Questions)
        {
            var answer = response.Answers.TryGetValue(question.Question, out var values) && values.Count > 0
                ? string.Join(", ", values)
                : "(no answer)";
            builder.Append('\n').Append("- ").Append(question.Question).Append(": ").Append(answer);
        }

        return builder.ToString();
    }
}
