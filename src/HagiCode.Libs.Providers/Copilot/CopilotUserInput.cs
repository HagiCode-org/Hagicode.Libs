using System.Text.Json;
using GitHub.Copilot;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Copilot;

/// <summary>
/// One question the Copilot agent asks through its <c>ask_user</c> tool. A Copilot question has one question, string
/// choices without descriptions, and one string answer.
/// </summary>
/// <param name="RequestId">
/// The question id the answer is written back for. It is the <c>ToolCallId</c> of the matching <c>user_input.requested</c>
/// event, else that event's <c>RequestId</c>, else a generated id that is unique within the session.
/// </param>
/// <param name="Question">The question text. It is also the key of the answer.</param>
/// <param name="Choices">The offered choices, in the order Copilot sent them. Empty for a free-text question.</param>
/// <param name="AllowFreeform">
/// Whether the answer may be text that is not one of the choices. A question without choices accepts free text whatever
/// this value says.
/// </param>
public sealed record CopilotUserInputRequest(
    string RequestId,
    string Question,
    IReadOnlyList<string> Choices,
    bool AllowFreeform)
{
    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the CLI message that carries a question. The provider yields each question to its
    /// caller as this message type, in the position it arrived in the stream.
    /// </summary>
    public const string MessageType = "question.request";

    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the CLI message that closes a question Copilot completed without a HagiCode answer,
    /// for example one answered in another client.
    /// </summary>
    public const string ClosedMessageType = "question.closed";

    /// <summary>
    /// The name of the Copilot tool that asks questions.
    /// </summary>
    public const string AskUserToolName = "ask_user";

    /// <summary>
    /// Reads a question from a <see cref="MessageType" /> stream message.
    /// </summary>
    /// <param name="message">The stream message.</param>
    /// <param name="request">The question, when the method returns <see langword="true" />.</param>
    /// <returns><see langword="false" /> when the message is not a question message or its content is not a readable question.</returns>
    public static bool TryFromCliMessage(CliMessage message, out CopilotUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(message);
        request = null!;
        if (message.Type != MessageType || message.Content.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (ReadString(message.Content, "request_id") is not { Length: > 0 } requestId ||
            ReadString(message.Content, "question") is not { Length: > 0 } question)
        {
            return false;
        }

        var choices = new List<string>();
        if (message.Content.TryGetProperty("choices", out var choiceElements) && choiceElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var choiceElement in choiceElements.EnumerateArray())
            {
                if (choiceElement.ValueKind != JsonValueKind.String || choiceElement.GetString() is not { } choice)
                {
                    return false;
                }

                choices.Add(choice);
            }
        }

        request = new CopilotUserInputRequest(
            requestId,
            question,
            choices,
            message.Content.TryGetProperty("allow_freeform", out var allowFreeform) && allowFreeform.ValueKind == JsonValueKind.True);
        return true;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}

/// <summary>
/// The answer a <see cref="CopilotOptions.UserInputHandler" /> returns for one question.
/// </summary>
/// <param name="Answers">
/// For the question text, exactly one value: the chosen choice, or the text the user typed when free text is allowed. A
/// missing or empty answer, more than one value, and text that is not an offered choice while free text is not allowed
/// decline the question. A question without choices accepts any one non-empty value.
/// </param>
public sealed record CopilotUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers)
{
    /// <summary>
    /// Converts the answer into the SDK response for <paramref name="request" />.
    /// </summary>
    /// <returns><see langword="null" /> when the answer is refused, so the caller declines the question.</returns>
    internal UserInputResponse? ToSdkResponse(CopilotUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Answers is null ||
            !Answers.TryGetValue(request.Question, out var values) ||
            values is not { Count: 1 } ||
            values[0] is not { } value ||
            string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var isChoice = request.Choices.Contains(value, StringComparer.Ordinal);
        if (!isChoice && request.Choices.Count > 0 && !request.AllowFreeform)
        {
            return null;
        }

        return new UserInputResponse
        {
            Answer = value,
            WasFreeform = !isChoice
        };
    }
}

/// <summary>
/// Thrown from the SDK user-input callback when a question is declined: the user gave no answer, the answer was refused, the
/// question timed out, or the handler failed. The encoding is gate G2 (unverified): the callback fails so that Copilot is
/// expected to be told that no answer was given, and the run continues with its own result.
/// </summary>
internal sealed class CopilotUserInputDeclinedException(string requestId)
    : InvalidOperationException($"The user gave no answer to Copilot question '{requestId}'.")
{
    /// <summary>
    /// Gets the id of the declined question.
    /// </summary>
    public string RequestId { get; } = requestId;
}
