using System.Text.Json;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.OpenCode;

/// <summary>
/// One option of an interactive OpenCode question.
/// </summary>
/// <param name="Label">The option text. It is the value sent back when the option is chosen.</param>
/// <param name="Description">Secondary text that explains the option. Empty when the agent sent none.</param>
public sealed record OpenCodeUserQuestionOption(string Label, string Description);

/// <summary>
/// One question of an interactive OpenCode <c>question</c> form. It maps to one form field, by position.
/// </summary>
/// <param name="Question">The question text. It is also the key of the answer.</param>
/// <param name="Header">A short label for the question. Empty when the agent sent none.</param>
/// <param name="MultiSelect">Whether the user may pick more than one option (a <c>multiselect</c> field).</param>
/// <param name="Options">The offered options, in the order the agent sent them.</param>
public sealed record OpenCodeUserQuestion(
    string Question,
    string Header,
    bool MultiSelect,
    IReadOnlyList<OpenCodeUserQuestionOption> Options);

/// <summary>
/// A question the OpenCode agent asks through its built-in <c>question</c> tool, read from an OpenCode form of kind
/// <c>question</c>.
/// </summary>
/// <param name="RequestId">The form id (<c>frm_...</c>). It is unique per question and is the id the answer is written back for.</param>
/// <param name="SessionId">The OpenCode session the question belongs to.</param>
/// <param name="ToolCallId">The id of the tool call that asked the question. Diagnostic only; null when OpenCode sent none.</param>
/// <param name="Questions">The questions in the order the agent asked them.</param>
public sealed record OpenCodeUserInputRequest(
    string RequestId,
    string SessionId,
    string? ToolCallId,
    IReadOnlyList<OpenCodeUserQuestion> Questions)
{
    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the CLI message that carries a question. When a user-input handler is set, the
    /// provider yields each question to its caller as this message type, in the position it arrived in the stream.
    /// </summary>
    public const string MessageType = "question";

    /// <summary>
    /// The name of OpenCode's built-in tool that asks questions.
    /// </summary>
    public const string QuestionToolName = "question";

    /// <summary>
    /// Reads a question from a <see cref="MessageType" /> stream message.
    /// </summary>
    /// <returns><see langword="false" /> when the message is not a question message or its content is not a readable question.</returns>
    public static bool TryFromCliMessage(CliMessage message, out OpenCodeUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(message);
        request = null!;
        if (message.Type != MessageType || message.Content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var content = message.Content;
        if (ReadString(content, "requestId") is not { Length: > 0 } requestId ||
            ReadString(content, "sessionId") is not { Length: > 0 } sessionId ||
            !content.TryGetProperty("questions", out var questionElements) ||
            questionElements.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var questions = new List<OpenCodeUserQuestion>();
        foreach (var questionElement in questionElements.EnumerateArray())
        {
            if (ReadString(questionElement, "question") is not { Length: > 0 } text)
            {
                return false;
            }

            var options = new List<OpenCodeUserQuestionOption>();
            if (questionElement.TryGetProperty("options", out var optionElements) && optionElements.ValueKind == JsonValueKind.Array)
            {
                foreach (var optionElement in optionElements.EnumerateArray())
                {
                    options.Add(new OpenCodeUserQuestionOption(
                        ReadString(optionElement, "label") ?? string.Empty,
                        ReadString(optionElement, "description") ?? string.Empty));
                }
            }

            questions.Add(new OpenCodeUserQuestion(
                text,
                ReadString(questionElement, "header") ?? string.Empty,
                questionElement.TryGetProperty("multiSelect", out var multiSelect) && multiSelect.ValueKind == JsonValueKind.True,
                options));
        }

        request = new OpenCodeUserInputRequest(requestId, sessionId, ReadString(content, "toolCallId"), questions);
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

/// <summary>
/// The answers a <see cref="OpenCodeOptions.UserInputHandler" /> returns for one request.
/// </summary>
/// <param name="Answers">
/// For each question text, the selected option labels or the free text the user typed. A value that is not one of the offered
/// labels is sent unchanged. A question with no entry, or with only empty values, is left unanswered; OpenCode then reports it
/// to the agent as unanswered. Two questions of one request with the same text share one entry, so they receive the same answer.
/// </param>
public sealed record OpenCodeUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers);

/// <summary>
/// The question handling of one interactive turn, passed to <see cref="OpenCodeHttpClient.PromptAsync(string, OpenCodeSessionPromptRequest, OpenCodeTurnInteraction?, CancellationToken)"/>.
/// </summary>
/// <param name="Handler">
/// Produces the answers for one question. It runs on a background task and receives a token that is cancelled when the question
/// timeout passes, the turn ends, or the caller cancels. Returning <see langword="null"/>, timing out, or throwing declines the question.
/// </param>
/// <param name="QuestionTimeout">How long a question may stay unanswered. Positive, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
/// <param name="OnQuestionAsked">Called once per question, on the turn's reading thread, before its handler starts. Optional.</param>
public sealed record OpenCodeTurnInteraction(
    Func<OpenCodeUserInputRequest, CancellationToken, Task<OpenCodeUserInputResponse?>> Handler,
    TimeSpan QuestionTimeout,
    Action<OpenCodeUserInputRequest>? OnQuestionAsked = null);

/// <summary>
/// The OpenCode question mapping: form detection, the form to question mapping by position, the answer encoding by field
/// type, and the messages that decline a question.
/// </summary>
public static class OpenCodeUserInput
{
    /// <summary>
    /// The message the agent sees when the handler returns no answer, returns a failure, or the answer is refused.
    /// </summary>
    public const string DeclinedMessage = "The user declined to answer this question.";

    /// <summary>
    /// The message the agent sees when the question timeout passes before an answer arrives.
    /// </summary>
    public const string TimeoutMessage = "No answer arrived before the question timed out, so the question was closed.";

    /// <summary>
    /// The message the agent sees when the run has no user-input handler, so the user cannot be asked.
    /// </summary>
    public const string NoHandlerMessage = "The user cannot be asked questions in this run, so the question was closed without an answer.";

    /// <summary>
    /// The message the agent sees when the run ends (the turn completes, fails or is cancelled) while the question is pending.
    /// </summary>
    public const string TurnEndedMessage = "The run ended before the user answered this question.";

    /// <summary>
    /// Reads a question from an OpenCode form.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when the form is not of kind <see cref="OpenCodeFormKinds.Question" />, or it has no field, or a field has
    /// neither a question text nor a header. An unreadable question form is declined rather than left pending.
    /// </returns>
    public static bool TryCreateRequest(OpenCodeFormInfo form, out OpenCodeUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(form);
        request = null!;
        if (!string.Equals(form.Kind, OpenCodeFormKinds.Question, StringComparison.Ordinal) || form.Fields.Count == 0)
        {
            return false;
        }

        var questions = new List<OpenCodeUserQuestion>(form.Fields.Count);
        foreach (var field in form.Fields)
        {
            var text = FirstNonEmpty(field.Description, field.Title);
            if (text is null)
            {
                return false;
            }

            var options = field.Options
                .Select(static option => new OpenCodeUserQuestionOption(
                    FirstNonEmpty(option.Label, option.Value) ?? string.Empty,
                    option.Description ?? string.Empty))
                .ToArray();
            questions.Add(new OpenCodeUserQuestion(
                text,
                field.Title ?? string.Empty,
                string.Equals(field.Type, "multiselect", StringComparison.Ordinal),
                options));
        }

        request = new OpenCodeUserInputRequest(form.Id, form.SessionId ?? string.Empty, form.Metadata?.Tool?.Id, questions);
        return true;
    }

    /// <summary>
    /// Builds the reply body for a question form: one entry per answered question, keyed <c>q{index}</c> by its position. A
    /// <c>multiselect</c> question takes a list of strings; a single-choice question takes one string, with several values joined
    /// by <c>", "</c>. An unanswered question has no entry, so OpenCode reports it as unanswered.
    /// </summary>
    internal static IReadOnlyDictionary<string, object> CreateFormAnswer(OpenCodeUserInputRequest request, OpenCodeUserInputResponse response)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var answer = new Dictionary<string, object>(StringComparer.Ordinal);
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index];
            if (response.Answers is null ||
                !response.Answers.TryGetValue(question.Question, out var values) ||
                values is null)
            {
                continue;
            }

            var cleaned = values.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (cleaned.Length == 0)
            {
                continue;
            }

            answer[FieldKey(index)] = question.MultiSelect
                ? cleaned.ToList()
                : string.Join(", ", cleaned);
        }

        return answer;
    }

    /// <summary>
    /// The field key of the question at <paramref name="index"/>: <c>q0</c>, <c>q1</c>, ...
    /// </summary>
    internal static string FieldKey(int index)
    {
        return "q" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rejects a question timeout that is neither positive nor infinite. Called before any request is sent.
    /// </summary>
    internal static void ValidateQuestionTimeout(TimeSpan questionTimeout, string parameterName)
    {
        if (questionTimeout != Timeout.InfiniteTimeSpan && questionTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                questionTimeout,
                "QuestionTimeout must be positive or Timeout.InfiniteTimeSpan.");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }
}
