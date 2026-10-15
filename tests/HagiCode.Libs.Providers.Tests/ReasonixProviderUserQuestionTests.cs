using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Reasonix;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Reasonix interactive questions replayed from the transcripts recorded from Reasonix v2.41.0. The scripted transport waits
/// for the provider's own answer to each question request before it replays what Reasonix did next.
/// </summary>
public sealed class ReasonixProviderUserQuestionTests
{
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("ask-single", 0)]
    [InlineData("ask-delayed-answer", 300)]
    public async Task A_question_answered_with_a_label_selects_its_option_and_the_turn_completes(string fixture, int answerDelayMilliseconds)
    {
        var requests = new List<ReasonixUserInputRequest>();
        var provider = new QuestionFixtureProvider(fixture);

        var messages = await RunAsync(provider, Handler(async (request, token) =>
        {
            requests.Add(request);
            await Task.Delay(answerDelayMilliseconds, token);
            return Answer(request, "Blue");
        }));

        var request = requests.ShouldHaveSingleItem();
        request.Question.Header.ShouldBe("Color");
        request.Question.Options.Select(static option => option.Label).ShouldBe(["Red", "Blue"]);
        SentOutcomes(provider).ShouldBe(["selected:q1:2"]);
        messages.Count(static message => message.Type == ReasonixUserInputRequest.MessageType).ShouldBe(1);
        AssistantText(messages).ShouldContain("Blue");
        var terminal = messages[^1];
        terminal.Type.ShouldBe("terminal.completed");
        terminal.Content.TryGetProperty("question_declined", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Two_questions_are_asked_one_after_the_other_and_the_second_only_after_the_first_is_answered()
    {
        var provider = new QuestionFixtureProvider("ask-two-questions");
        var asked = new List<string>();
        var answersSentBeforeEachQuestion = new List<int>();

        var messages = await RunAsync(provider, Handler((request, _) =>
        {
            asked.Add(request.RequestId);
            answersSentBeforeEachQuestion.Add(provider.Transports.Single().SentAnswers.Count);
            return Task.FromResult<ReasonixUserInputResponse?>(Answer(request, request.Question.Id == "q1" ? "Red" : "Large"));
        }));

        asked.ShouldBe(["ask-22bc089b-1-q1", "ask-22bc089b-1-q2"]);
        answersSentBeforeEachQuestion.ShouldBe([0, 1]);
        SentOutcomes(provider).ShouldBe(["selected:q1:1", "selected:q2:2"]);
        messages.Where(static message => message.Type == ReasonixUserInputRequest.MessageType)
            .Select(static message => message.Content.GetProperty("request_id").GetString())
            .ShouldBe(["ask-22bc089b-1-q1", "ask-22bc089b-1-q2"]);
        AssistantText(messages).ShouldContain("Red, Large");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task A_multi_select_question_is_reported_as_multi_select_and_answered_with_one_label()
    {
        var provider = new QuestionFixtureProvider("ask-multi");
        ReasonixUserInputRequest? seen = null;

        var messages = await RunAsync(provider, Handler((request, _) =>
        {
            seen = request;
            return Task.FromResult<ReasonixUserInputResponse?>(Answer(request, "Cheese"));
        }));

        seen.ShouldNotBeNull().Question.MultiSelect.ShouldBeTrue();
        SentOutcomes(provider).ShouldBe(["selected:q1:1"]);
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task Free_text_declines_with_the_cancel_option_and_the_error_turn_ends_as_a_completion()
    {
        var provider = new QuestionFixtureProvider("ask-reject-option");

        var messages = await RunAsync(provider, Handler((request, _) =>
            Task.FromResult<ReasonixUserInputResponse?>(Answer(request, "Purple, please"))));

        SentOutcomes(provider).ShouldBe(["selected:q1:cancel"]);
        AssertCompletedAfterDecline(messages);
    }

    [Fact]
    public async Task A_decline_without_the_cancel_option_sends_cancelled_and_the_error_turn_ends_as_a_completion()
    {
        var transcript = ReasonixTranscriptFixtures.Load("ask-cancelled-outcome");
        RemoveCancelOptions(transcript);
        var provider = new QuestionFixtureProvider(transcript);

        var messages = await RunAsync(provider, Handler((_, _) => Task.FromResult<ReasonixUserInputResponse?>(null)));

        SentOutcomes(provider).ShouldBe(["cancelled"]);
        AssertCompletedAfterDecline(messages);
    }

    [Fact]
    public async Task A_question_timeout_declines_and_the_turn_ends_as_a_completion()
    {
        var provider = new QuestionFixtureProvider("ask-reject-option");
        CancellationToken handlerToken = default;

        var messages = await RunAsync(
            provider,
            Handler(async (_, token) =>
            {
                handlerToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }),
            questionTimeout: TimeSpan.FromMilliseconds(200));

        handlerToken.IsCancellationRequested.ShouldBeTrue();
        SentOutcomes(provider).ShouldBe(["selected:q1:cancel"]);
        AssertCompletedAfterDecline(messages);
    }

    [Fact]
    public async Task An_error_turn_without_a_declined_question_is_still_a_failure()
    {
        var provider = new QuestionFixtureProvider("ask-reject-option");

        var messages = await RunAsync(provider, Handler((request, _) =>
            Task.FromResult<ReasonixUserInputResponse?>(Answer(request, "Red"))));

        SentOutcomes(provider).ShouldBe(["selected:q1:1"]);
        var terminal = messages[^1];
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("error");
        terminal.Content.TryGetProperty("question_declined", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_turn_that_continues_after_the_cancel_option_ends_as_a_normal_completion()
    {
        var provider = new QuestionFixtureProvider("ask-single");

        var messages = await RunAsync(provider, Handler((_, _) => Task.FromResult<ReasonixUserInputResponse?>(null)));

        SentOutcomes(provider).ShouldBe(["selected:q1:cancel"]);
        var terminal = messages[^1];
        terminal.Type.ShouldBe("terminal.completed");
        terminal.Content.TryGetProperty("question_declined", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_caller_stop_while_a_question_is_pending_keeps_the_caller_outcome_and_reports_no_decline()
    {
        var provider = new QuestionFixtureProvider("ask-stop-pending");
        var handlerStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource(RunTimeout);
        var messages = new List<CliMessage>();
        var options = new ReasonixOptions
        {
            UserInputHandler = async (_, token) =>
            {
                handlerStarted.TrySetResult(token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
        };

        var exception = await Record.ExceptionAsync(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(options, "ask", caller.Token))
            {
                messages.Add(message);
                if (message.Type == ReasonixUserInputRequest.MessageType)
                {
                    await handlerStarted.Task.WaitAsync(RunTimeout);
                    await caller.CancelAsync();
                }
            }
        });

        exception.ShouldBeAssignableTo<OperationCanceledException>();
        (await handlerStarted.Task).IsCancellationRequested.ShouldBeTrue();
        messages.ShouldContain(static message => message.Type == ReasonixUserInputRequest.MessageType);
        messages.Where(static message => message.Content.TryGetProperty("question_declined", out _)).ShouldBeEmpty();
        SentOutcomes(provider).ShouldNotContain("selected:q1:cancel");
        SentOutcomes(provider).ShouldAllBe(static outcome => outcome == "cancelled");
        provider.Transports.Single().IsTerminated.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_handler_a_question_is_answered_with_its_first_option_and_a_warning_names_it()
    {
        var logger = new CapturingLogger();
        var provider = new QuestionFixtureProvider("ask-single", logger);

        var messages = await RunAsync(provider, handler: null);

        SentOutcomes(provider).ShouldBe(["selected:q1:1"]);
        messages.ShouldNotContain(static message => message.Type == ReasonixUserInputRequest.MessageType);
        messages.ShouldContain(static message => message.Type == "session.notification" &&
                                                 message.Content.GetProperty("method").GetString() == "session/request_permission");
        provider.OneArgumentSessionClientCalls.ShouldBe(1);
        var warning = logger.Entries.Where(static entry => entry.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain("ask-1f339eb0-1-q1");
        warning.Message.ShouldContain("Which color should I use?");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task With_a_handler_tool_approvals_never_reach_it_and_are_answered_as_without_one()
    {
        var withoutHandler = new QuestionFixtureProvider("permission-turn");
        await RunAsync(withoutHandler, handler: null);
        var calls = 0;
        var withHandler = new QuestionFixtureProvider("permission-turn");

        var messages = await RunAsync(withHandler, Handler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<ReasonixUserInputResponse?>(null);
        }));

        calls.ShouldBe(0);
        withHandler.OneArgumentSessionClientCalls.ShouldBe(0);
        SentOutcomes(withHandler).ShouldBe(SentOutcomes(withoutHandler));
        SentOutcomes(withHandler).ShouldNotBeEmpty();
        messages.ShouldNotContain(static message => message.Type == ReasonixUserInputRequest.MessageType);
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task The_question_message_follows_the_ask_tool_call_and_precedes_its_update_and_is_not_assistant_text()
    {
        var provider = new QuestionFixtureProvider("ask-single");

        var messages = await RunAsync(provider, Handler((request, _) =>
            Task.FromResult<ReasonixUserInputResponse?>(Answer(request, "Red"))));

        var types = messages.Select(static message => message.Type).ToList();
        var askCall = messages.FindIndex(static message => message.Type == "tool.call" &&
                                                           message.Content.GetProperty("update").GetProperty("title").GetString() ==
                                                           ReasonixUserInputRequest.QuestionToolName);
        var question = types.IndexOf(ReasonixUserInputRequest.MessageType);
        var askUpdate = types.IndexOf("tool.update");
        askCall.ShouldBeGreaterThanOrEqualTo(0);
        askCall.ShouldBeLessThan(question);
        question.ShouldBeLessThan(askUpdate);
        ReasonixUserInputRequest.TryFromCliMessage(messages[question], out var parsed).ShouldBeTrue();
        parsed.RequestId.ShouldBe("ask-1f339eb0-1-q1");
        AssistantText(messages).ShouldBe("Blue");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_resumed_session_can_ask_and_answer_a_question(bool loadInsteadOfResume)
    {
        var transcript = loadInsteadOfResume
            ? Compose("load-replay", "ask-single", removeResumeCapability: true)
            : Compose("ask-resume-pending", "ask-single", removeResumeCapability: false);
        var provider = new QuestionFixtureProvider(transcript);

        var messages = await RunAsync(
            provider,
            Handler((request, _) => Task.FromResult<ReasonixUserInputResponse?>(Answer(request, "Blue"))),
            sessionId: "fixture-session-01");

        var methods = provider.Transports.Single().SentMethods;
        methods.ShouldContain(loadInsteadOfResume ? "session/load" : "session/resume");
        messages[0].Type.ShouldBe("session.resumed");
        messages.Count(static message => message.Type == ReasonixUserInputRequest.MessageType).ShouldBe(1);
        SentOutcomes(provider).ShouldBe(["selected:q1:2"]);
        AssistantText(messages).ShouldBe("Blue");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task A_resumed_session_holding_an_unanswered_question_runs_its_next_prompt_normally()
    {
        var provider = new QuestionFixtureProvider("ask-resume-pending");
        var calls = 0;

        var messages = await RunAsync(
            provider,
            Handler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult<ReasonixUserInputResponse?>(null);
            }),
            sessionId: "fixture-session-01");

        calls.ShouldBe(0);
        AssistantText(messages).ShouldBe("resumed");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1000)]
    public async Task An_invalid_question_timeout_with_a_handler_is_rejected_before_any_process_starts(int milliseconds)
    {
        var provider = new QuestionFixtureProvider("ask-single");

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await RunAsync(
            provider,
            Handler((_, _) => Task.FromResult<ReasonixUserInputResponse?>(null)),
            questionTimeout: TimeSpan.FromMilliseconds(milliseconds)));

        exception.ShouldBeAssignableTo<ArgumentException>();
        provider.Transports.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_infinite_question_timeout_is_accepted()
    {
        var provider = new QuestionFixtureProvider("ask-single");

        var messages = await RunAsync(
            provider,
            Handler((request, _) => Task.FromResult<ReasonixUserInputResponse?>(Answer(request, "Blue"))),
            questionTimeout: Timeout.InfiniteTimeSpan);

        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task An_invalid_question_timeout_without_a_handler_is_ignored()
    {
        var provider = new QuestionFixtureProvider("text-turn");

        var messages = await RunAsync(provider, handler: null, questionTimeout: TimeSpan.Zero);

        AssistantText(messages).ShouldBe("pong");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    // ---------------------------------------------------------------- helpers

    private static Func<ReasonixUserInputRequest, CancellationToken, Task<ReasonixUserInputResponse?>> Handler(
        Func<ReasonixUserInputRequest, CancellationToken, Task<ReasonixUserInputResponse?>> handler) => handler;

    private static async Task<List<CliMessage>> RunAsync(
        QuestionFixtureProvider provider,
        Func<ReasonixUserInputRequest, CancellationToken, Task<ReasonixUserInputResponse?>>? handler,
        TimeSpan? questionTimeout = null,
        string? sessionId = null)
    {
        using var timeout = new CancellationTokenSource(RunTimeout);
        var options = new ReasonixOptions
        {
            SessionId = sessionId,
            UserInputHandler = handler,
            QuestionTimeout = questionTimeout ?? ReasonixOptions.DefaultQuestionTimeout
        };
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, "ask", timeout.Token))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static void AssertCompletedAfterDecline(IReadOnlyList<CliMessage> messages)
    {
        messages.ShouldNotContain(static message => message.Type == "terminal.failed");
        var terminal = messages[^1];
        terminal.Type.ShouldBe("terminal.completed");
        terminal.Content.GetProperty("question_declined").GetBoolean().ShouldBeTrue();
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("error");
    }

    private static ReasonixUserInputResponse Answer(ReasonixUserInputRequest request, params string[] values)
    {
        return new ReasonixUserInputResponse(new Dictionary<string, IReadOnlyList<string>> { [request.Question.Question] = values });
    }

    /// <summary>Describes every permission answer the provider sent as <c>selected:&lt;id&gt;</c> or <c>cancelled</c>.</summary>
    private static List<string> SentOutcomes(QuestionFixtureProvider provider)
    {
        return provider.Transports.Single().SentAnswers
            .Select(static answer => answer["result"]?["outcome"])
            .OfType<JsonNode>()
            .Select(static outcome => outcome["outcome"]!.GetValue<string>() == "selected"
                ? "selected:" + outcome["optionId"]!.GetValue<string>()
                : outcome["outcome"]!.GetValue<string>())
            .ToList();
    }

    private static string AssistantText(IEnumerable<CliMessage> messages)
    {
        return string.Concat(messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.TryGetProperty("text", out var text) ? text.GetString() : null));
    }

    private static void RemoveCancelOptions(IEnumerable<ReasonixTranscriptRecord> transcript)
    {
        foreach (var record in transcript.Where(static record => record.Method == "session/request_permission"))
        {
            var options = (JsonArray)record.Message["params"]!["options"]!;
            for (var index = options.Count - 1; index >= 0; index--)
            {
                if (options[index]!["optionId"]!.GetValue<string>().EndsWith(":cancel", StringComparison.Ordinal))
                {
                    options.RemoveAt(index);
                }
            }
        }
    }

    /// <summary>
    /// Joins the session opening of <paramref name="opening" /> with the prompt turn of <paramref name="turn" />, so a resumed
    /// or loaded session asks the question recorded in a new session.
    /// </summary>
    private static List<ReasonixTranscriptRecord> Compose(string opening, string turn, bool removeResumeCapability)
    {
        var openingRecords = ReasonixTranscriptFixtures.Load(opening);
        var turnRecords = ReasonixTranscriptFixtures.Load(turn);
        var composed = openingRecords.Take(IndexOfPrompt(openingRecords)).Concat(turnRecords.Skip(IndexOfPrompt(turnRecords))).ToList();
        if (removeResumeCapability)
        {
            var initializeResult = composed.First(static record => record.Direction == "in" && record.IsResponse).Message["result"]!;
            ((JsonObject)initializeResult["agentCapabilities"]!["sessionCapabilities"]!).Remove("resume");
        }

        return composed;
    }

    private static int IndexOfPrompt(IReadOnlyList<ReasonixTranscriptRecord> records)
    {
        for (var index = 0; index < records.Count; index++)
        {
            if (records[index].Direction == "out" && records[index].Method == "session/prompt")
            {
                return index;
            }
        }

        throw new InvalidOperationException("The transcript has no session/prompt request.");
    }

    private sealed class QuestionFixtureProvider : ReasonixProvider
    {
        private readonly IReadOnlyList<ReasonixTranscriptRecord> _transcript;

        public QuestionFixtureProvider(string fixture, ILogger<ReasonixProvider>? logger = null)
            : this(ReasonixTranscriptFixtures.Load(fixture), logger)
        {
        }

        public QuestionFixtureProvider(IReadOnlyList<ReasonixTranscriptRecord> transcript, ILogger<ReasonixProvider>? logger = null)
            : base(new StubExecutableResolver(), new CliProcessManager(), new StubRuntimeEnvironmentResolver(), logger)
        {
            _transcript = transcript;
        }

        public List<ReasonixScriptedAcpTransport> Transports { get; } = [];

        public int OneArgumentSessionClientCalls { get; private set; }

        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
        {
            OneArgumentSessionClientCalls++;
            return base.CreateSessionClient(startContext);
        }

        protected override IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
        {
            var transport = new ReasonixScriptedAcpTransport(_transcript, awaitAgentRequestAnswers: true);
            Transports.Add(transport);
            return transport;
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class StubRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>());
        }
    }

    private sealed class CapturingLogger : ILogger<ReasonixProvider>
    {
        private readonly object _sync = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_sync)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_sync)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
