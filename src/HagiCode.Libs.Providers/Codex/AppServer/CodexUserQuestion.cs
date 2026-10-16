namespace HagiCode.Libs.Providers.Codex.AppServer;

/// <summary>
/// One selectable option of a Codex user question.
/// </summary>
/// <param name="Label">The option text. It is the value sent back when the option is chosen.</param>
/// <param name="Description">Secondary text that explains the option. Empty when Codex sent none.</param>
public sealed record CodexUserQuestionOption(string Label, string Description);

/// <summary>
/// One question of a <see cref="CodexUserQuestionRequest"/>.
/// </summary>
/// <param name="Id">The question id. It is the key of the answer in the response.</param>
/// <param name="Header">A short label for the question. Empty when Codex sent none.</param>
/// <param name="Question">The question text.</param>
/// <param name="IsOther">Whether the user may type an answer that is not one of the offered options.</param>
/// <param name="IsSecret">Whether the answer is sensitive. The answer is delivered unchanged; the caller decides how to store it.</param>
/// <param name="Options">The offered options. Empty means free text only.</param>
public sealed record CodexUserQuestion(
    string Id,
    string Header,
    string Question,
    bool IsOther,
    bool IsSecret,
    IReadOnlyList<CodexUserQuestionOption> Options);

/// <summary>
/// The <c>item/tool/requestUserInput</c> server request that codex app-server raises while a turn runs.
/// </summary>
/// <remarks>
/// <see cref="IsBlocking"/> is what Codex reported. On codex-cli 0.163.0 every observed request carried <c>false</c>, and
/// the turn still waited for a reply, so the transport answers every request and does not rely on the flag.
/// <see cref="AutoResolutionMs"/> is deprecated and read only; no timer is started from it.
/// </remarks>
/// <param name="RequestId">The JSON-RPC request id as text. The transport uses it to reply to the request exactly once.</param>
/// <param name="ThreadId">The Codex thread the request belongs to.</param>
/// <param name="TurnId">The Codex turn the request belongs to.</param>
/// <param name="ItemId">The Codex item that asked the question.</param>
/// <param name="IsBlocking">The <c>isBlocking</c> value reported by Codex. A missing value is reported as <see langword="true"/>.</param>
/// <param name="Questions">The questions, in the order Codex asked them.</param>
/// <param name="AutoResolutionMs">The deprecated <c>autoResolutionMs</c> value, or <see langword="null"/> when Codex sent none.</param>
public sealed record CodexUserQuestionRequest(
    string RequestId,
    string ThreadId,
    string TurnId,
    string ItemId,
    bool IsBlocking,
    IReadOnlyList<CodexUserQuestion> Questions,
    long? AutoResolutionMs);

/// <summary>
/// The answers a user-input handler returns for one <see cref="CodexUserQuestionRequest"/>.
/// </summary>
/// <param name="Answers">For each question id, the selected option labels or the free text. Values are passed through unchanged.</param>
public sealed record CodexUserQuestionResponse(IReadOnlyDictionary<string, IReadOnlyList<string>> Answers);

/// <summary>
/// The delegate that answers a Codex user question. It receives a token that is cancelled when the question timeout passes
/// or the run is stopped. Returning <see langword="null"/>, timing out or throwing declines the question.
/// </summary>
public delegate Task<CodexUserQuestionResponse?> CodexUserInputHandler(
    CodexUserQuestionRequest request,
    CancellationToken cancellationToken);
