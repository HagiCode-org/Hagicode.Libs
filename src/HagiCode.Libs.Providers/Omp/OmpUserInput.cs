using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Omp;

/// <summary>
/// One option of an interactive question.
/// </summary>
/// <param name="Label">The option text. It is the value sent back when the option is chosen.</param>
/// <param name="Description">Secondary text that explains the option. Empty when the agent sent none.</param>
public sealed record OmpUserQuestionOption(string Label, string Description);

/// <summary>
/// One question of an interactive <c>ask</c> request.
/// </summary>
/// <param name="Id">The id OMP gave the question. The answer frame names the question by it.</param>
/// <param name="Question">The question text. It is also the key of the answer.</param>
/// <param name="Header">A short label for the question. Empty when the agent sent none.</param>
/// <param name="MultiSelect">Whether the user may pick more than one option.</param>
/// <param name="Options">The offered options.</param>
public sealed record OmpUserQuestion(
    string Id,
    string Question,
    string Header,
    bool MultiSelect,
    IReadOnlyList<OmpUserQuestionOption> Options);

/// <summary>
/// An interactive question the OMP agent asks through its built-in <c>ask</c> tool.
/// </summary>
/// <param name="RequestId">
/// The id of the <c>extension_ui_request</c> frame that carries the request. It is unique per <c>ask</c> call and stable for
/// the run, and the answer is written back for it.
/// </param>
/// <param name="Questions">The questions in the order the agent asked them.</param>
public sealed record OmpUserInputRequest(
    string RequestId,
    IReadOnlyList<OmpUserQuestion> Questions)
{
    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the CLI message that carries a request. The provider yields an interactive
    /// request to its caller as this message type, in the position it arrived in the stream.
    /// </summary>
    public const string MessageType = "extension_ui_request";

    /// <summary>
    /// The name of OMP's built-in tool that asks interactive questions.
    /// </summary>
    public const string QuestionToolName = "ask";

    internal const string AskMethod = "ask";

    /// <summary>
    /// Reads an interactive question request from an <c>extension_ui_request</c> stream message.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when the message is not an <c>ask</c> request or its content is not valid. OMP's per-option
    /// <c>preview</c>, per-question <c>recommended</c> and the frame's <c>timeout</c> are not read.
    /// </returns>
    public static bool TryFromCliMessage(CliMessage message, out OmpUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(message);
        request = null!;
        return message.Type == MessageType && TryParse(message.Content, out request);
    }

    internal static bool TryParse(JsonElement frame, out OmpUserInputRequest request)
    {
        request = null!;
        if (frame.ValueKind != JsonValueKind.Object
            || ReadString(frame, "method") != AskMethod
            || ReadString(frame, "id") is not { Length: > 0 } requestId
            || !frame.TryGetProperty("questions", out var questionElements)
            || questionElements.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var questions = new List<OmpUserQuestion>();
        foreach (var questionElement in questionElements.EnumerateArray())
        {
            if (ReadString(questionElement, "id") is not { Length: > 0 } id
                || ReadString(questionElement, "question") is not { Length: > 0 } text)
            {
                return false;
            }

            var options = new List<OmpUserQuestionOption>();
            if (questionElement.TryGetProperty("options", out var optionElements)
                && optionElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var optionElement in optionElements.EnumerateArray())
                {
                    if (ReadString(optionElement, "label") is not { } label)
                    {
                        return false;
                    }

                    options.Add(new OmpUserQuestionOption(label, ReadString(optionElement, "description") ?? string.Empty));
                }
            }

            questions.Add(new OmpUserQuestion(
                id,
                text,
                ReadString(questionElement, "header") ?? string.Empty,
                questionElement.TryGetProperty("multi", out var multi) && multi.ValueKind == JsonValueKind.True,
                options));
        }

        if (questions.Count == 0)
        {
            return false;
        }

        request = new OmpUserInputRequest(requestId, questions);
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
/// The answers a <see cref="OmpOptions.UserInputHandler" /> returns for one request.
/// </summary>
/// <param name="Answers">
/// For each question text, the selected option labels or the free text the user typed. The values of one question are
/// read as one text joined by a comma and a space: a text that is an offered label (for a multi-select question, distinct
/// offered labels joined by a comma and a space) selects those options, any other text is sent as free text.
/// </param>
public sealed record OmpUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers)
{
    /// <summary>
    /// Converts the answers into the entries of OMP's answer frame: one per question, in the order of the request.
    /// </summary>
    /// <returns><see langword="null" /> when a question has no answer, so the request is answered as cancelled instead.</returns>
    internal IReadOnlyList<OmpAskAnswer>? ToAskAnswers(OmpUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answers = new List<OmpAskAnswer>(request.Questions.Count);
        foreach (var question in request.Questions)
        {
            if (!Answers.TryGetValue(question.Question, out var values) || values is not { Count: > 0 })
            {
                return null;
            }

            var text = string.Join(", ", values);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            answers.Add(OmpAskAnswer.From(question, text));
        }

        return answers;
    }
}

/// <summary>
/// One entry of OMP's <c>extension_ui_response</c> to an <c>ask</c> request. OMP rejects more than one selected option on
/// a single-select question, an unknown label and a label selected twice, so <see cref="From" /> only selects offered labels
/// and sends everything else as <see cref="CustomInput" />.
/// </summary>
/// <param name="Id">The id of the question.</param>
/// <param name="SelectedOptions">Exact offered labels, distinct, at most one for a single-select question.</param>
/// <param name="CustomInput">The free text, when the answer is not a selection.</param>
internal sealed record OmpAskAnswer(string Id, IReadOnlyList<string> SelectedOptions, string? CustomInput)
{
    public const string Separator = ", ";

    public static OmpAskAnswer From(OmpUserQuestion question, string text)
    {
        var labels = question.Options.Select(static option => option.Label).ToArray();
        if (!question.MultiSelect)
        {
            return labels.Contains(text, StringComparer.Ordinal)
                ? new OmpAskAnswer(question.Id, [text], null)
                : new OmpAskAnswer(question.Id, [], text);
        }

        var candidates = labels
            .Where(static label => label.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static label => label.Length)
            .ToArray();
        return SplitIntoLabels(text, 0, candidates, []) is { } selected
            ? new OmpAskAnswer(question.Id, selected, null)
            : new OmpAskAnswer(question.Id, [], text);
    }

    /// <summary>
    /// Reads <paramref name="text" /> from <paramref name="start" /> as distinct labels separated by
    /// <see cref="Separator" />, trying the longest label first because a label may contain the separator itself.
    /// </summary>
    private static List<string>? SplitIntoLabels(string text, int start, string[] candidates, HashSet<string> used)
    {
        foreach (var label in candidates)
        {
            if (used.Contains(label) || !text.AsSpan(start).StartsWith(label, StringComparison.Ordinal))
            {
                continue;
            }

            var end = start + label.Length;
            if (end == text.Length)
            {
                return [label];
            }

            if (!text.AsSpan(end).StartsWith(Separator, StringComparison.Ordinal))
            {
                continue;
            }

            used.Add(label);
            var rest = SplitIntoLabels(text, end + Separator.Length, candidates, used);
            used.Remove(label);
            if (rest is not null)
            {
                rest.Insert(0, label);
                return rest;
            }
        }

        return null;
    }
}
