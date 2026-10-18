using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Contract tests of the interactive question path against a real <c>opencode</c> executable and a real model. They spend a few
/// model calls and create a session in the OpenCode data directory of the current user, so they run only when
/// <c>HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION</c> is <c>1</c> or <c>true</c>.
/// <c>HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION_MODEL</c> picks the model as <c>&lt;provider&gt;/&lt;model&gt;</c>; without it
/// OpenCode uses its configured default. <c>HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION_EXECUTABLE</c> picks a specific
/// executable, otherwise <c>opencode</c> is resolved from <c>PATH</c>. Each run asks one single-choice question: the first run
/// answers it, the second declines it, and both must finish with a terminal message.
/// </summary>
[Category("Integration")]
public sealed class OpenCodeRealCliUserQuestionTests
{
    private const string SwitchVariable = "HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION";
    private const string ExecutableVariable = "HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION_EXECUTABLE";
    private const string ModelVariable = "HAGICODE_LIBS_REAL_CLI_OPENCODE_USER_QUESTION_MODEL";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task One_question_is_asked_and_its_answer_reaches_the_agent()
    {
        ExternalGate.Require(SwitchVariable);

        var asked = new List<OpenCodeUserInputRequest>();
        var options = CreateOptions(request =>
        {
            asked.Add(request);
            return Task.FromResult<OpenCodeUserInputResponse?>(AnswerEvery(request, "Blue"));
        });

        var messages = await RunAsync(
            options,
            "Use the question tool exactly once, before anything else, to ask me ONE single-choice question: which colour do I prefer, options Red and Blue (Red first). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool.");

        var request = asked.ShouldHaveSingleItem();
        request.Questions.Single().Options.Select(static option => option.Label).ShouldBe(["Red", "Blue"]);
        request.Questions.Single().MultiSelect.ShouldBeFalse();
        messages.Select(static message => message.Type).ShouldContain(OpenCodeUserInputRequest.MessageType);
        messages.Select(static message => message.Type).ShouldContain("terminal.completed");
        AssistantText(messages).ShouldContain("Blue", Case.Insensitive);
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task One_question_declined_lets_the_run_finish_with_a_terminal_message()
    {
        ExternalGate.Require(SwitchVariable);

        var asked = new List<OpenCodeUserInputRequest>();
        var options = CreateOptions(request =>
        {
            asked.Add(request);
            return Task.FromResult<OpenCodeUserInputResponse?>(null);
        });

        var messages = await RunAsync(
            options,
            "Use the question tool exactly once, before anything else, to ask me ONE single-choice question: which colour do I prefer, options Red and Blue (Red first). Whatever I answer, reply in one short sentence. Do not use any other tool.");

        asked.ShouldHaveSingleItem();
        messages.Select(static message => message.Type).ShouldContain(OpenCodeUserInputRequest.MessageType);
        messages.Select(static message => message.Type).ShouldContain("terminal.completed");
    }

    private static OpenCodeOptions CreateOptions(Func<OpenCodeUserInputRequest, Task<OpenCodeUserInputResponse?>> handler)
    {
        return new OpenCodeOptions
        {
            ExecutablePath = NullIfEmpty(Environment.GetEnvironmentVariable(ExecutableVariable)),
            Model = NullIfEmpty(Environment.GetEnvironmentVariable(ModelVariable)),
            QuestionTimeout = TimeSpan.FromMinutes(2),
            UserInputHandler = (request, _) => handler(request),
        };
    }

    private static async Task<List<CliMessage>> RunAsync(OpenCodeOptions options, string prompt)
    {
        await using var provider = new OpenCodeProvider(new CliExecutableResolver());
        using var timeout = new CancellationTokenSource(RunTimeout);
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, timeout.Token))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static string AssistantText(IEnumerable<CliMessage> messages)
    {
        return string.Join(
            Environment.NewLine,
            messages.Where(static message => message.Type == "assistant")
                .Select(static message => message.Content.GetProperty("text").GetString()));
    }

    private static OpenCodeUserInputResponse AnswerEvery(OpenCodeUserInputRequest request, string answer)
    {
        return new OpenCodeUserInputResponse(request.Questions.ToDictionary(
            static question => question.Question,
            _ => (IReadOnlyList<string>)[answer],
            StringComparer.Ordinal));
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
