using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Transport;

namespace HagiCode.Libs.Providers.Reasonix;

/// <summary>
/// One option of a Reasonix question.
/// </summary>
/// <param name="Label">The option text. Answering with exactly this text selects the option.</param>
/// <param name="Description">Secondary text that explains the option. Empty when the agent sent none.</param>
public sealed record ReasonixUserQuestionOption(string Label, string Description);

/// <summary>
/// One question that Reasonix's built-in <c>ask</c> tool asks through ACP.
/// </summary>
/// <param name="Id">The id Reasonix gave the question inside its <c>ask</c> call, for example <c>q1</c>.</param>
/// <param name="Question">The question text. It is also the key of the answer.</param>
/// <param name="Header">A short label for the question. Empty when the agent sent none.</param>
/// <param name="MultiSelect">
/// Whether the agent marked the question as multi-select, as Reasonix reported it. ACP answers a question with one option
/// only, so a multi-select question still accepts exactly one label.
/// </param>
/// <param name="Options">The offered options, without Reasonix's own cancel option.</param>
public sealed record ReasonixUserQuestion(
    string Id,
    string Question,
    string Header,
    bool MultiSelect,
    IReadOnlyList<ReasonixUserQuestionOption> Options);

/// <summary>
/// A question the Reasonix agent asks through its built-in <c>ask</c> tool. Reasonix sends one ACP
/// <c>session/request_permission</c> request per question, and the next question of the same call only after the previous
/// one is answered.
/// </summary>
/// <param name="RequestId">
/// The tool call id of the permission request, for example <c>ask-1f339eb0-1-q1</c>. It is unique per question within a
/// Reasonix session and is the id the answer is written back for.
/// </param>
/// <param name="Question">The one question of the request.</param>
public sealed record ReasonixUserInputRequest(string RequestId, ReasonixUserQuestion Question)
{
    /// <summary>
    /// The <see cref="CliMessage.Type" /> of the CLI message that carries a question. When a user-input handler is set, the
    /// provider yields each question request to its caller as this message type, in the position it arrived in the stream.
    /// </summary>
    public const string MessageType = "question.request";

    /// <summary>
    /// The name of Reasonix's built-in tool that asks questions. The <c>tool.call</c> of that tool carries every question of
    /// the call in its raw input; the questions themselves arrive as separate requests.
    /// </summary>
    public const string QuestionToolName = "ask";

    internal const string PermissionMethod = "session/request_permission";

    private static readonly Regex QuestionToolCallIdPattern = new(
        @"^ask-(?:\S+-)?\d+-q(?<number>\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Gets the option id of every offered label. When two options share a label, the first one wins.
    /// </summary>
    internal IReadOnlyDictionary<string, string> OptionIdsByLabel { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the id of Reasonix's cancel option (<c>qN:cancel</c>), or <see langword="null" /> when the request offers none.
    /// </summary>
    internal string? CancelOptionId { get; init; }

    /// <summary>
    /// Reads a question request from a <see cref="MessageType" /> stream message.
    /// </summary>
    /// <param name="message">The stream message.</param>
    /// <param name="request">The question, when the method returns <see langword="true" />.</param>
    /// <returns><see langword="false" /> when the message is not a question message or its content is not a readable question.</returns>
    public static bool TryFromCliMessage(CliMessage message, out ReasonixUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(message);
        request = null!;
        return message.Type == MessageType &&
               message.Content.ValueKind == JsonValueKind.Object &&
               message.Content.TryGetProperty("params", out var parameters) &&
               TryParse(AcpPermissionRequest.FromParameters(parameters), out request);
    }

    /// <summary>
    /// Returns <see langword="true" /> when the permission request is a Reasonix question: its tool call id has the form
    /// <c>ask-…-&lt;turn&gt;-qN</c> and every option id starts with <c>qN:</c>. Tool approvals (<c>gate-*</c>) are not questions.
    /// </summary>
    internal static bool IsQuestionRequest(AcpPermissionRequest permission)
    {
        return TryGetQuestionNumber(permission, out _);
    }

    /// <summary>
    /// Reads a Reasonix question from a permission request.
    /// </summary>
    /// <returns>
    /// <see langword="false" /> when the request is not a question, or is a question without an option the user could choose
    /// or without question text.
    /// </returns>
    internal static bool TryParse(AcpPermissionRequest permission, out ReasonixUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(permission);
        request = null!;
        if (!TryGetQuestionNumber(permission, out var number))
        {
            return false;
        }

        var prefix = "q" + number.ToString(CultureInfo.InvariantCulture) + ":";
        var toolCall = GetProperty(permission.Parameters, "toolCall");
        var rawInput = GetProperty(toolCall, "rawInput");
        var rawOptions = GetProperty(rawInput, "options");

        var options = new List<ReasonixUserQuestionOption>();
        var optionIdsByLabel = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cancelOptionId = null;
        foreach (var option in permission.Options)
        {
            var suffix = option.OptionId[prefix.Length..];
            if (string.Equals(suffix, "cancel", StringComparison.Ordinal))
            {
                cancelOptionId ??= option.OptionId;
                continue;
            }

            if (!int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 1)
            {
                continue;
            }

            var rawOption = rawOptions.ValueKind == JsonValueKind.Array && index <= rawOptions.GetArrayLength()
                ? rawOptions[index - 1]
                : default;
            var label = ReadString(rawOption, "label") is { Length: > 0 } rawLabel ? rawLabel : option.Name;
            options.Add(new ReasonixUserQuestionOption(label, ReadString(rawOption, "description") ?? string.Empty));
            optionIdsByLabel.TryAdd(label, option.OptionId);
        }

        var text = FirstNonEmpty(ReadString(rawInput, "question"), ReadString(toolCall, "title"));
        if (options.Count == 0 || text is null)
        {
            return false;
        }

        request = new ReasonixUserInputRequest(
            permission.ToolCallId!,
            new ReasonixUserQuestion(
                ReadString(rawInput, "id") is { Length: > 0 } id ? id : prefix[..^1],
                text,
                ReadContentText(GetProperty(toolCall, "content")),
                GetProperty(rawInput, "multi").ValueKind == JsonValueKind.True,
                options))
        {
            OptionIdsByLabel = optionIdsByLabel,
            CancelOptionId = cancelOptionId
        };
        return true;
    }

    /// <summary>
    /// Creates the decision that declines a question: Reasonix's cancel option when the request offers it, otherwise the
    /// <c>cancelled</c> outcome.
    /// </summary>
    internal static AcpPermissionDecision CreateDecline(AcpPermissionRequest permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        return TryGetQuestionNumber(permission, out var number) &&
               permission.Options.FirstOrDefault(option => string.Equals(
                   option.OptionId,
                   "q" + number.ToString(CultureInfo.InvariantCulture) + ":cancel",
                   StringComparison.Ordinal)) is { } cancelOption
            ? AcpPermissionDecision.Select(cancelOption.OptionId)
            : AcpPermissionDecision.Cancelled;
    }

    /// <summary>
    /// Creates the decision that declines this question.
    /// </summary>
    internal AcpPermissionDecision CreateDecline()
    {
        return CancelOptionId is { } cancelOptionId
            ? AcpPermissionDecision.Select(cancelOptionId)
            : AcpPermissionDecision.Cancelled;
    }

    private static bool TryGetQuestionNumber(AcpPermissionRequest permission, out int number)
    {
        number = 0;
        if (permission.ToolCallId is not { } toolCallId ||
            QuestionToolCallIdPattern.Match(toolCallId) is not { Success: true } match ||
            !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
        {
            return false;
        }

        var prefix = "q" + number.ToString(CultureInfo.InvariantCulture) + ":";
        return permission.Options.All(option => option.OptionId.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private static string ReadContentText(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;
            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    builder.Append(ReadContentText(item));
                }

                return builder.ToString();
            case JsonValueKind.Object:
                if (ReadString(content, "text") is { } text)
                {
                    return text;
                }

                return ReadContentText(GetProperty(content, "content"));
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// Reads a property by name, ignoring case: Reasonix 2.41.0 sends <c>Label</c> and <c>Description</c> in the raw input.
    /// </summary>
    private static JsonElement GetProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return default;
        }

        if (element.TryGetProperty(propertyName, out var exact))
        {
            return exact;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        var property = GetProperty(element, propertyName);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }
}

/// <summary>
/// The answer a <see cref="ReasonixOptions.UserInputHandler" /> returns for one question.
/// </summary>
/// <param name="Answers">
/// For the question text, the chosen option label. Reasonix answers a question with one option, so the answer must be
/// exactly one value that equals an offered label. A missing or empty answer, text that is not an offered label, and more
/// than one value decline the question.
/// </param>
public sealed record ReasonixUserInputResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers)
{
    /// <summary>
    /// Converts the answer into the permission decision sent to Reasonix: <c>selected qN:&lt;i&gt;</c> for an offered label,
    /// otherwise the decline of <paramref name="request" />.
    /// </summary>
    internal AcpPermissionDecision ToOutcome(ReasonixUserInputRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Answers is null ||
            !Answers.TryGetValue(request.Question.Question, out var values) ||
            values is not { Count: 1 } ||
            values[0] is not { Length: > 0 } label ||
            !request.OptionIdsByLabel.TryGetValue(label, out var optionId))
        {
            return request.CreateDecline();
        }

        return AcpPermissionDecision.Select(optionId);
    }
}
