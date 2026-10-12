using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Shouldly;
using HagiCode.Libs.Core.AgentWork;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

// Interactive questions: the control_request / control_response conversation of --permission-prompt-tool stdio. The
// streams come from ClaudeUserQuestionCapture (real CLI output); the transport is scripted, answers the host's writes the
// way the CLI does (it echoes every control_response) and lets a test decide when each message arrives.
public sealed partial class ClaudeCodeProviderTests
{
    private const string FirstQuestionText = "Which colour do you prefer?";

    public static TheoryData<string> CliVersions => new() { "2.1.159", "2.1.292" };

    private static IReadOnlyList<CliMessage> CapturedSingle(string version) =>
        version == "2.1.159" ? ClaudeUserQuestionCapture.V2159Single : ClaudeUserQuestionCapture.V2292Single;

    private static CliMessage CapturedSingleHost(string version) =>
        version == "2.1.159" ? ClaudeUserQuestionCapture.V2159SingleHostResponse : ClaudeUserQuestionCapture.V2292SingleHostResponse;

    private static IReadOnlyList<CliMessage> CapturedMulti(string version) =>
        version == "2.1.159" ? ClaudeUserQuestionCapture.V2159Multi : ClaudeUserQuestionCapture.V2292Multi;

    private static CliMessage CapturedMultiHost(string version) =>
        version == "2.1.159" ? ClaudeUserQuestionCapture.V2159MultiHostResponse : ClaudeUserQuestionCapture.V2292MultiHostResponse;

    private static IReadOnlyList<CliMessage> CapturedDenied(string version) =>
        version == "2.1.159" ? ClaudeUserQuestionCapture.V2159Denied : ClaudeUserQuestionCapture.V2292Denied;

    private static IReadOnlyList<CliMessage> CapturedWriteDenied(string version) =>
        version == "2.1.159" ? ClaudeUserQuestionCapture.V2159WriteDenied : ClaudeUserQuestionCapture.V2292WriteDenied;

    // Capture layout: init, prompt replay, assistant tool_use, control_request, echo of the host's response, the tool
    // result, the assistant's closing text, result. The scripted transport produces the echo itself.
    private static IReadOnlyList<CliMessage> UntilRequest(IReadOnlyList<CliMessage> capture) => capture.Take(4).ToArray();

    private static IReadOnlyList<CliMessage> AfterAnswer(IReadOnlyList<CliMessage> capture) => capture.Skip(5).ToArray();

    private static ClaudeUserInputResponse Answers(params (string Question, string[] Values)[] answers) =>
        new(answers.ToDictionary(static pair => pair.Question, static pair => (IReadOnlyList<string>)pair.Values));

    [Fact]
    public void BuildCommandArguments_adds_the_permission_prompt_tool_after_the_replay_flag_only_with_a_handler()
    {
        var provider = CreateProvider();

        var withHandler = provider.BuildCommandArguments(new ClaudeCodeOptions
        {
            Model = "haiku",
            UserInputHandler = static (_, _) => Task.FromResult<ClaudeUserInputResponse?>(null)
        });
        var withoutHandler = provider.BuildCommandArguments(new ClaudeCodeOptions { Model = "haiku" });

        var index = withHandler.ToList().IndexOf("--permission-prompt-tool");
        index.ShouldBeGreaterThan(0);
        withHandler[index + 1].ShouldBe("stdio");
        withHandler[index - 1].ShouldBe("--replay-user-messages");
        withoutHandler.ShouldNotContain("--permission-prompt-tool");
        withoutHandler.ShouldBe(withHandler.Where((_, position) => position != index && position != index + 1).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ExecuteAsync_rejects_a_non_positive_question_timeout(int seconds)
    {
        var provider = CreateProvider();
        var options = new ClaudeCodeOptions { QuestionTimeout = TimeSpan.FromSeconds(seconds) };

        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await CollectAsync(provider, options));
    }

    [Fact]
    public async Task ExecuteAsync_accepts_an_infinite_question_timeout()
    {
        var provider = CreateProvider();

        var messages = await CollectAsync(provider, new ClaudeCodeOptions { QuestionTimeout = Timeout.InfiniteTimeSpan });

        messages.ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(CliVersions))]
    public void Captures_have_the_layout_the_scripted_transport_relies_on(string version)
    {
        foreach (var capture in new[] { CapturedSingle(version), CapturedMulti(version), CapturedDenied(version), CapturedWriteDenied(version) })
        {
            DescribeTypes(capture).ShouldBe(
                ["system:init", "user", "assistant", "control_request", "control_response", "user", "assistant", "result"]);
        }
    }

    [Theory]
    [MemberData(nameof(CliVersions))]
    public void The_cli_echo_of_a_control_response_is_not_a_prompt_replay_and_is_recognized_as_an_echo(string version)
    {
        var echo = CapturedSingle(version)[4];
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.RecordHostPromptSent();

        ClaudeControlProtocol.IsControlResponseEcho(echo).ShouldBeTrue();
        ClaudeStreamCompletionTracker.IsPromptReplay(echo).ShouldBeFalse();

        // Even an echo that reached the tracker would neither start the prompt's turn nor end the run.
        tracker.Observe(echo).ShouldBeFalse();
        tracker.HostPromptAwaitingStart.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(CliVersions))]
    public void TryFromCliMessage_reads_the_question_request_of_both_cli_versions(string version)
    {
        var request = CapturedSingle(version)[3];

        ClaudeUserInputRequest.TryFromCliMessage(request, out var parsed).ShouldBeTrue();

        parsed.RequestId.ShouldBe("req-fixture-1");
        parsed.ToolUseId.ShouldBe("toolu-fixture-1");
        parsed.ToolName.ShouldBe("AskUserQuestion");
        var question = parsed.Questions.ShouldHaveSingleItem();
        question.Question.ShouldBe(FirstQuestionText);
        question.Header.ShouldStartWith("Colour");
        question.MultiSelect.ShouldBeFalse();
        question.Options.Select(static option => option.Label).ShouldBe(["Red", "Blue"]);
        question.Options.ShouldAllBe(static option => option.Description.Length > 0);
        parsed.Input.GetProperty("questions").GetArrayLength().ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(CliVersions))]
    public void TryFromCliMessage_does_not_read_a_permission_request_for_another_tool(string version)
    {
        ClaudeUserInputRequest.TryFromCliMessage(CapturedWriteDenied(version)[3], out _).ShouldBeFalse();
        ClaudeUserInputRequest.TryFromCliMessage(CapturedSingle(version)[2], out _).ShouldBeFalse();
    }

    [Fact]
    public void ClaudeUserInputRequest_reports_requires_user_interaction_only_where_the_cli_sends_it()
    {
        ClaudeControlProtocol.TryParseRequest(ClaudeUserQuestionCapture.V2292Single[3], out var current).ShouldBeTrue();
        ClaudeControlProtocol.TryParseRequest(ClaudeUserQuestionCapture.V2159Single[3], out var shipped).ShouldBeTrue();

        current.RequiresUserInteraction.ShouldBeTrue();
        shipped.RequiresUserInteraction.ShouldBeFalse();
        current.IsUserQuestion.ShouldBeTrue();
        shipped.IsUserQuestion.ShouldBeTrue();
    }

    [Fact]
    public void The_tracker_suspends_every_bound_while_a_question_is_pending_and_restores_them_afterwards()
    {
        var tracker = new ClaudeStreamCompletionTracker(TimeSpan.FromMinutes(1));
        tracker.Observe(StreamMessage("system", new { subtype = "init" }));
        tracker.Observe(MonitorToolUse());
        tracker.Observe(StreamMessage(
            "system",
            new { subtype = "task_started", task_id = "monitor-1", task_type = "local_bash", tool_use_id = "toolu-monitor" }));
        tracker.Observe(StreamMessage("result", new { is_error = false }));
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.MainAgentBackgroundWork);

        tracker.RecordQuestionPending();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.UserQuestion);

        tracker.RecordQuestionPending();
        tracker.RecordQuestionResolved();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.UserQuestion);

        tracker.RecordQuestionResolved();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.MainAgentBackgroundWork);

        // A resolve without a pending question must not turn the counter negative and hide the next question.
        tracker.RecordQuestionResolved();
        tracker.RecordQuestionPending();
        tracker.CurrentWait.ShouldBe(ClaudeStreamCompletionTracker.PendingWait.UserQuestion);
    }

    [Theory]
    [MemberData(nameof(CliVersions))]
    public async Task ExecuteAsync_answers_a_single_choice_question_with_the_label_of_the_chosen_option(string version)
    {
        var capture = CapturedSingle(version);
        var transport = new QuestionTransport();
        var provider = new QuestionProvider(transport);
        var requests = new List<ClaudeUserInputRequest>();
        var options = QuestionOptions(
            (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<ClaudeUserInputResponse?>(Answers((FirstQuestionText, ["Blue"])));
            });

        var run = new QuestionRun(provider, options);
        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the answer");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        requests.ShouldHaveSingleItem().Questions.ShouldHaveSingleItem().Question.ShouldBe(FirstQuestionText);
        JsonNode.DeepEquals(ToNode(transport.ControlResponses().Single()), ToNode(CapturedSingleHost(version))).ShouldBeTrue();
        var allow = ToNode(transport.ControlResponses().Single())!["response"]!["response"]!;
        allow["behavior"]!.GetValue<string>().ShouldBe("allow");
        allow["updatedInput"]!["answers"]![FirstQuestionText]!.GetValue<string>().ShouldBe("Blue");
        allow["updatedInput"]!["questions"]!.AsArray().Count.ShouldBe(1);

        // The conversation continues: the tool result and the closing text arrive, the echo does not.
        DescribeTypes(run.Messages).ShouldBe(["system:init", "assistant", "control_request", "user", "assistant", "result"]);
        run.Messages[^1].Content.GetProperty("result").GetString().ShouldBe("ANSWER=Blue");
    }

    [Fact]
    public async Task ExecuteAsync_sends_free_text_that_matches_no_option_unchanged()
    {
        var capture = CapturedSingle("2.1.292");
        const string FreeText = "Green, \"with quotes\" and a\nnew line";
        var transport = new QuestionTransport();
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => Task.FromResult<ClaudeUserInputResponse?>(Answers((FirstQuestionText, [FreeText])))));

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the answer");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var written = ToNode(transport.ControlResponses().Single())!["response"]!["response"]!["updatedInput"]!["answers"]!;
        written[FirstQuestionText]!.GetValue<string>().ShouldBe(FreeText);

        // The response is one JSON line: the quote and the new line of the answer are escaped, not concatenated.
        transport.WrittenLines().ShouldAllBe(static line => !line.Contains('\n'));
    }

    [Theory]
    [MemberData(nameof(CliVersions))]
    public async Task ExecuteAsync_joins_multi_select_answers_with_a_comma_and_a_space(string version)
    {
        var capture = CapturedMulti(version);
        var transport = new QuestionTransport();
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((request, _) =>
            {
                request.Questions.ShouldHaveSingleItem().MultiSelect.ShouldBeTrue();
                return Task.FromResult<ClaudeUserInputResponse?>(
                    Answers((request.Questions[0].Question, ["Dark theme", "Mobile layout", "Custom thing"])));
            }));

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the answer");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        JsonNode.DeepEquals(ToNode(transport.ControlResponses().Single()), ToNode(CapturedMultiHost(version))).ShouldBeTrue();
        run.Messages[^1].Content.GetProperty("result").GetString().ShouldBe("ANSWER=Dark theme, Mobile layout, Custom thing");
    }

    [Fact]
    public async Task ExecuteAsync_answers_every_question_of_one_request_keyed_by_its_text()
    {
        var transport = new QuestionTransport();
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => Task.FromResult<ClaudeUserInputResponse?>(Answers(
                ("Targets?", ["Linux", "macOS"]),
                ("Run the tests?", ["No"])))));

        transport.Push(
            StreamMessage("system", new { subtype = "init" }),
            ReplayMessage(),
            TwoQuestionRequest("req-two", "toolu-two"));
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the answer");
        transport.Push(StreamMessage("result", new { is_error = false }));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var written = ToNode(transport.ControlResponses().Single())!["response"]!;
        written["request_id"]!.GetValue<string>().ShouldBe("req-two");
        var answers = written["response"]!["updatedInput"]!["answers"]!.AsObject();
        answers.Select(static pair => pair.Key).ShouldBe(["Targets?", "Run the tests?"]);
        answers["Targets?"]!.GetValue<string>().ShouldBe("Linux, macOS");
        answers["Run the tests?"]!.GetValue<string>().ShouldBe("No");
    }

    [Fact]
    public async Task ExecuteAsync_keeps_streaming_while_a_question_is_pending_and_yields_the_request_in_order()
    {
        var capture = CapturedSingle("2.1.292");
        var transport = new QuestionTransport();
        var answer = new TaskCompletionSource<ClaudeUserInputResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) =>
            {
                handlerStarted.TrySetResult();
                return answer.Task;
            }));

        transport.Push([.. UntilRequest(capture)]);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The request reached the caller, after the output that preceded it, although the handler has not returned.
        await WaitUntilAsync(() => run.Messages.Any(static message => message.Type == "control_request"), "the request");
        DescribeTypes(run.Messages).ShouldBe(["system:init", "assistant", "control_request"]);
        answer.Task.IsCompleted.ShouldBeFalse();

        // Output that the CLI emits meanwhile is delivered before the answer exists.
        transport.Push(StreamMessage("assistant", new { message = new { content = new[] { new { type = "text", text = "meanwhile" } } } }));
        await WaitUntilAsync(() => run.Messages.Count == 4, "the output that arrived while the question was pending");
        run.Messages[^1].Type.ShouldBe("assistant");
        transport.ControlResponses().ShouldBeEmpty();

        answer.SetResult(Answers((FirstQuestionText, ["Red"])));
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the answer");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ExecuteAsync_yields_the_request_in_order_while_the_turn_delivery_holds_the_turn()
    {
        // The turn starts before the prompt is replayed, so the delivery holds it; the request is held with it and comes
        // out in its place when the replay releases the turn.
        var capture = CapturedSingle("2.1.292");
        var transport = new QuestionTransport();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) =>
            {
                handlerStarted.TrySetResult();
                return new TaskCompletionSource<ClaudeUserInputResponse?>().Task;
            }));

        transport.Push(capture[0], capture[2], capture[3]);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        run.Messages.ShouldBeEmpty();

        transport.Push(capture[1]);
        await WaitUntilAsync(() => run.Messages.Count == 3, "the released turn");

        DescribeTypes(run.Messages).ShouldBe(["system:init", "assistant", "control_request"]);
        await run.CancelAndWaitAsync();
    }

    [Theory]
    [MemberData(nameof(CliVersions))]
    public async Task ExecuteAsync_denies_a_permission_request_for_another_tool_without_asking_the_handler(string version)
    {
        var capture = CapturedWriteDenied(version);
        var transport = new QuestionTransport();
        var handlerCalls = 0;
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<ClaudeUserInputResponse?>(null);
            }));

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the deny");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        handlerCalls.ShouldBe(0);
        var decision = ToNode(transport.ControlResponses().Single())!["response"]!;
        decision["request_id"]!.GetValue<string>().ShouldBe("req-fixture-1");
        decision["response"]!["behavior"]!.GetValue<string>().ShouldBe("deny");
        decision["response"]!["message"]!.GetValue<string>().ShouldBe(ClaudeControlProtocol.FixedDenyMessage);

        // A request that the host answered itself is not conversation and is not passed on.
        DescribeTypes(run.Messages).ShouldBe(["system:init", "assistant", "user", "assistant", "result"]);
    }

    [Fact]
    public async Task ExecuteAsync_denies_a_question_when_no_handler_is_set_and_does_not_hang()
    {
        var capture = CapturedSingle("2.1.292");
        var transport = new QuestionTransport();
        var run = new QuestionRun(new QuestionProvider(transport), new ClaudeCodeOptions { SessionId = "session-1" });

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the deny");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var decision = ToNode(transport.ControlResponses().Single())!["response"]!["response"]!;
        decision["behavior"]!.GetValue<string>().ShouldBe("deny");
        decision["message"]!.GetValue<string>().ShouldBe(ClaudeControlProtocol.QuestionUnavailableMessage);
        DescribeTypes(run.Messages).ShouldNotContain("control_request");
    }

    [Fact]
    public async Task ExecuteAsync_writes_nothing_but_the_prompt_when_no_handler_is_set_and_the_cli_sends_no_request()
    {
        var transport = new QuestionTransport();
        var run = new QuestionRun(new QuestionProvider(transport), new ClaudeCodeOptions { SessionId = "session-1" });

        transport.Push(
            StreamMessage("system", new { subtype = "init" }),
            ReplayMessage(),
            StreamMessage("assistant", new { message = new { content = new[] { new { type = "text", text = "hi" } } } }),
            StreamMessage("result", new { is_error = false }));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        transport.Sent.Select(static message => message.Type).ShouldBe(["user"]);
    }

    [Fact]
    public async Task ExecuteAsync_ignores_a_control_request_of_another_subtype()
    {
        var transport = new QuestionTransport();
        var run = new QuestionRun(new QuestionProvider(transport), QuestionOptions());

        transport.Push(
            StreamMessage("system", new { subtype = "init" }),
            ReplayMessage(),
            StreamMessage("control_request", new { request_id = "req-hook", request = new { subtype = "hook_callback" } }),
            StreamMessage("result", new { is_error = false }));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        transport.ControlResponses().ShouldBeEmpty();
        DescribeTypes(run.Messages).ShouldNotContain("control_request");
    }

    [Fact]
    public async Task ExecuteAsync_does_not_let_the_existing_wait_bounds_end_a_run_that_waits_for_an_answer()
    {
        // After the first turn a Monitor still runs, so the run is in a bounded wait: its timeout plus the grace period.
        // A question that arrives then suspends that bound. Once it is answered the bound applies again, and since the
        // time has passed meanwhile the run ends with what it received.
        var transport = new QuestionTransport();
        var answer = new TaskCompletionSource<ClaudeUserInputResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => answer.Task) with { BackgroundWorkGracePeriod = TimeSpan.FromMilliseconds(100) });

        transport.Push(
            StreamMessage("system", new { subtype = "init" }),
            ReplayMessage(),
            MonitorToolUse(timeoutMilliseconds: 100),
            StreamMessage(
                "system",
                new { subtype = "task_started", task_id = "monitor-1", task_type = "local_bash", tool_use_id = "toolu-monitor" }),
            StreamMessage("result", new { is_error = false }),
            TwoQuestionRequest("req-wait", "toolu-wait"));
        await WaitUntilAsync(() => run.Messages.Any(static message => message.Type == "control_request"), "the request");

        // Far longer than the declared timeout plus the grace period.
        await Task.Delay(700);
        run.Completion.IsCompleted.ShouldBeFalse();

        answer.SetResult(Answers(("Targets?", ["Linux"]), ("Run the tests?", ["Yes"])));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        transport.ControlResponses().Count.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_denies_a_question_that_stays_unanswered_past_the_question_timeout()
    {
        var capture = CapturedDenied("2.1.292");
        var transport = new QuestionTransport();
        CancellationToken handlerToken = default;
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions(async (_, token) =>
            {
                handlerToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }) with { QuestionTimeout = TimeSpan.FromMilliseconds(200) });

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the timeout deny");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var decision = ToNode(transport.ControlResponses().Single())!["response"]!["response"]!;
        decision["behavior"]!.GetValue<string>().ShouldBe("deny");
        decision["message"]!.GetValue<string>().ShouldBe(ClaudeControlProtocol.QuestionTimedOutMessage);
        handlerToken.IsCancellationRequested.ShouldBeTrue();

        // The run reaches its normal terminal result; nothing about it is an error.
        run.Messages[^1].Type.ShouldBe("result");
        ClaudeStreamCompletionTracker.IsErrorResult(run.Messages[^1].Content).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_denies_a_pending_question_when_the_caller_cancels_before_disposing_the_transport()
    {
        var capture = CapturedSingle("2.1.292");
        var transport = new QuestionTransport();
        using var cancellation = new CancellationTokenSource();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions(async (_, token) =>
            {
                handlerStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }),
            cancellation.Token);

        transport.Push([.. UntilRequest(capture)]);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        // The stream ends by cancellation, never by completing normally.
        await Should.ThrowAsync<OperationCanceledException>(async () => await run.Completion.WaitAsync(TimeSpan.FromSeconds(10)));

        var decision = ToNode(transport.ControlResponses().Single())!["response"]!["response"]!;
        decision["behavior"]!.GetValue<string>().ShouldBe("deny");
        decision["message"]!.GetValue<string>().ShouldBe(ClaudeControlProtocol.QuestionCancelledMessage);
        var events = transport.EventLog();
        events.IndexOf("send:control_response").ShouldBeGreaterThanOrEqualTo(0);
        events.IndexOf("dispose").ShouldBeGreaterThan(events.IndexOf("send:control_response"));
    }

    [Fact]
    public async Task ExecuteAsync_releases_a_pending_handler_when_the_run_ends_without_a_cancellation()
    {
        var capture = CapturedSingle("2.1.292");
        var transport = new QuestionTransport();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions(async (_, token) =>
            {
                handlerStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                catch (OperationCanceledException)
                {
                    handlerEnded.TrySetResult();
                    throw;
                }

                return null;
            }));

        transport.Push([.. UntilRequest(capture)]);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The CLI ends the stream while the question waits, for example because it was killed.
        transport.Complete();
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        await handlerEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        transport.ControlResponses().ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_denies_with_the_failure_message_when_the_handler_throws_and_keeps_the_run_going()
    {
        var capture = CapturedDenied("2.1.292");
        var transport = new QuestionTransport();
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => throw new InvalidOperationException("boom from the handler")));

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the deny");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var decision = ToNode(transport.ControlResponses().Single())!["response"]!["response"]!;
        decision["behavior"]!.GetValue<string>().ShouldBe("deny");
        decision["message"]!.GetValue<string>().ShouldContain("boom from the handler");
        run.Messages[^1].Type.ShouldBe("result");
    }

    [Fact]
    public async Task ExecuteAsync_denies_when_the_handler_returns_no_answer()
    {
        var capture = CapturedDenied("2.1.159");
        var transport = new QuestionTransport();
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => Task.FromResult<ClaudeUserInputResponse?>(null)));

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the deny");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var decision = ToNode(transport.ControlResponses().Single())!["response"]!["response"]!;
        decision["behavior"]!.GetValue<string>().ShouldBe("deny");
        decision["message"]!.GetValue<string>().ShouldBe(ClaudeControlProtocol.NoAnswerMessage);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_deliver_the_cli_echo_of_its_own_response()
    {
        var capture = CapturedSingle("2.1.292");
        var transport = new QuestionTransport { EchoControlResponses = true };
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => Task.FromResult<ClaudeUserInputResponse?>(Answers((FirstQuestionText, ["Red"])))));

        transport.Push([.. UntilRequest(capture)]);
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the answer");
        transport.Push([.. AfterAnswer(capture)]);
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        transport.Received.Count(static message => message.Type == "control_response").ShouldBe(1);
        run.Messages.ShouldNotContain(static message => message.Type == "control_response");
    }

    [Fact]
    public async Task ExecuteAsync_orders_a_question_between_the_agent_work_snapshots_around_it()
    {
        var transport = new QuestionTransport();
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => Task.FromResult<ClaudeUserInputResponse?>(Answers(("Targets?", ["Linux"]), ("Run the tests?", ["No"])))));

        transport.Push(
            StreamMessage("system", new { subtype = "init" }),
            ReplayMessage(),
            ToolUseMessage("Agent", "toolu-agent", new { description = "Review the plan", prompt = "go" }),
            StreamMessage(
                "system",
                new { subtype = "task_started", task_id = "agent-1", task_type = "local_agent", tool_use_id = "toolu-agent" }),
            TwoQuestionRequest("req-agent", "toolu-two"));
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the answer");
        transport.Push(
            StreamMessage("system", new { subtype = "task_notification", task_id = "agent-1", status = "completed", summary = "ok" }),
            StreamMessage("result", new { is_error = false }));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var types = DescribeTypes(run.Messages);
        var runningSnapshot = types.IndexOf(AgentWorkSnapshot.MessageType);
        var request = types.IndexOf("control_request");
        var completedSnapshot = types.LastIndexOf(AgentWorkSnapshot.MessageType);
        runningSnapshot.ShouldBeGreaterThan(-1);
        runningSnapshot.ShouldBeLessThan(request);
        completedSnapshot.ShouldBeGreaterThan(request);

        var snapshots = run.Messages
            .Where(static message => message.Type == AgentWorkSnapshot.MessageType)
            .Select(static message => AgentWorkSnapshot.TryFromCliMessage(message, out var snapshot) ? snapshot : null)
            .ToList();
        snapshots[0]!.Items.ShouldHaveSingleItem().Status.ShouldBe(AgentWorkStatuses.Running);
        snapshots[^1]!.Items.ShouldHaveSingleItem().Status.ShouldBe(AgentWorkStatuses.Completed);
    }

    [Fact]
    public async Task ExecuteAsync_writes_whole_lines_when_the_receive_loop_and_two_questions_write_at_once()
    {
        var transport = new QuestionTransport { ChunkedWrites = true };
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) => Task.FromResult<ClaudeUserInputResponse?>(Answers(("Targets?", ["Linux"]), ("Run the tests?", ["No"])))));

        // Two answers and a fixed deny are written by background tasks and by the receive loop in the same moment.
        transport.Push(
            StreamMessage("system", new { subtype = "init" }),
            ReplayMessage(),
            TwoQuestionRequest("req-a", "toolu-a"),
            TwoQuestionRequest("req-b", "toolu-b"),
            StreamMessage(
                "control_request",
                new
                {
                    request_id = "req-write",
                    request = new { subtype = "can_use_tool", tool_name = "Write", tool_use_id = "toolu-w", input = new { file_path = "/tmp/x" } }
                }));
        await WaitUntilAsync(() => transport.ControlResponses().Count == 3, "all three writes");
        transport.Push(StreamMessage("result", new { is_error = false }));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        transport.OverlappingWrites.ShouldBe(0);
        var lines = transport.WrittenLines();
        lines.Count.ShouldBe(4);
        foreach (var line in lines)
        {
            Should.NotThrow(() => JsonDocument.Parse(line));
        }

        transport.ControlResponses()
            .Select(static message => ToNode(message)!["response"]!["request_id"]!.GetValue<string>())
            .Order()
            .ShouldBe(["req-a", "req-b", "req-write"]);
    }

    [Fact]
    public async Task ExecuteAsync_gives_up_the_request_it_cannot_parse_as_a_question_with_a_deny()
    {
        // A question request without a tool_use_id cannot be tied to an assistant message and is not put to the user.
        var transport = new QuestionTransport();
        var handlerCalls = 0;
        var run = new QuestionRun(
            new QuestionProvider(transport),
            QuestionOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<ClaudeUserInputResponse?>(null);
            }));

        transport.Push(
            StreamMessage("system", new { subtype = "init" }),
            ReplayMessage(),
            StreamMessage(
                "control_request",
                new
                {
                    request_id = "req-broken",
                    request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", input = new { questions = new[] { new { question = "?" } } } }
                }));
        await WaitUntilAsync(() => transport.ControlResponses().Count == 1, "the deny");
        transport.Push(StreamMessage("result", new { is_error = false }));
        await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        handlerCalls.ShouldBe(0);
        ToNode(transport.ControlResponses().Single())!["response"]!["response"]!["behavior"]!.GetValue<string>().ShouldBe("deny");
    }

    private static ClaudeCodeOptions QuestionOptions(
        Func<ClaudeUserInputRequest, CancellationToken, Task<ClaudeUserInputResponse?>>? handler = null)
    {
        return new ClaudeCodeOptions
        {
            SessionId = "session-1",
            WorkingDirectory = "/tmp/project",
            UserInputHandler = handler ?? ((_, _) => Task.FromResult<ClaudeUserInputResponse?>(null))
        };
    }

    private static CliMessage ReplayMessage()
    {
        return StreamMessage("user", new { isReplay = true, message = new { role = "user", content = "hello" } });
    }

    private static CliMessage TwoQuestionRequest(string requestId, string toolUseId)
    {
        var input = new
        {
            questions = new object[]
            {
                new
                {
                    question = "Targets?",
                    header = "Targets",
                    multiSelect = true,
                    options = new object[]
                    {
                        new { label = "Linux", description = "Build for Linux" },
                        new { label = "macOS", description = "Build for macOS" }
                    }
                },
                new
                {
                    question = "Run the tests?",
                    header = "Tests",
                    multiSelect = false,
                    options = new object[] { new { label = "Yes", description = "Run them" }, new { label = "No", description = "Skip them" } }
                }
            }
        };
        return StreamMessage(
            "control_request",
            new
            {
                request_id = requestId,
                request = new { subtype = "can_use_tool", tool_name = "AskUserQuestion", tool_use_id = toolUseId, input }
            });
    }

    private static JsonNode? ToNode(CliMessage message) => JsonNode.Parse(message.Content.GetRawText());

    private static async Task WaitUntilAsync(Func<bool> condition, string description, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {description}.");
            }

            await Task.Delay(10);
        }
    }

    // Runs one execution in the background so a test can push messages and inspect the output while it is still going.
    private sealed class QuestionRun
    {
        private readonly object _lock = new();
        private readonly List<CliMessage> _messages = [];
        private readonly CancellationTokenSource _ownCancellation = new();

        public QuestionRun(ClaudeCodeProvider provider, ClaudeCodeOptions options, CancellationToken? cancellationToken = null)
        {
            var token = cancellationToken ?? _ownCancellation.Token;
            Completion = Task.Run(async () =>
            {
                await foreach (var message in provider.ExecuteAsync(options, "hello", token))
                {
                    lock (_lock)
                    {
                        _messages.Add(message);
                    }
                }
            });
        }

        public Task Completion { get; }

        public IReadOnlyList<CliMessage> Messages
        {
            get
            {
                lock (_lock)
                {
                    return _messages.ToArray();
                }
            }
        }

        public async Task CancelAndWaitAsync()
        {
            await _ownCancellation.CancelAsync();
            try
            {
                await Completion.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class QuestionProvider(QuestionTransport transport)
        : ClaudeCodeProvider(new StubExecutableResolver(), new StubCliProcessManager(), new StubRuntimeEnvironmentResolver())
    {
        protected override ICliTransport CreateTransport(HagiCode.Libs.Core.Process.ProcessStartContext startContext) => transport;
    }

    // A CLI that answers like the real one: it replays every line the host writes (a control_response comes back as itself)
    // and delivers whatever the test pushes, when the test pushes it.
    private sealed class QuestionTransport : ICliTransport
    {
        private readonly Channel<CliMessage> _inbound = Channel.CreateUnbounded<CliMessage>();
        private readonly object _lock = new();
        private readonly List<CliMessage> _sent = [];
        private readonly List<CliMessage> _received = [];
        private readonly List<string> _events = [];
        private readonly StringBuilder _written = new();
        private int _activeWrites;

        public bool IsConnected { get; private set; }

        public bool EchoControlResponses { get; init; } = true;

        // Writes each line in two halves with a pause in between, so two writers that are not serialized would mix lines.
        public bool ChunkedWrites { get; init; }

        public int OverlappingWrites { get; private set; }

        public IReadOnlyList<CliMessage> Sent
        {
            get
            {
                lock (_lock)
                {
                    return _sent.ToArray();
                }
            }
        }

        public IReadOnlyList<CliMessage> Received
        {
            get
            {
                lock (_lock)
                {
                    return _received.ToArray();
                }
            }
        }

        public IReadOnlyList<CliMessage> ControlResponses() =>
            Sent.Where(static message => message.Type == "control_response").ToArray();

        public List<string> EventLog()
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }

        public List<string> WrittenLines()
        {
            lock (_lock)
            {
                return _written.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            }
        }

        public void Push(params CliMessage[] messages)
        {
            foreach (var message in messages)
            {
                _inbound.Writer.TryWrite(message);
            }
        }

        public void Complete() => _inbound.Writer.TryComplete();

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            return Task.CompletedTask;
        }

        public async Task SendAsync(CliMessage message, CancellationToken cancellationToken = default)
        {
            var json = message.Content.GetRawText();
            if (Interlocked.Increment(ref _activeWrites) > 1)
            {
                OverlappingWrites++;
            }

            try
            {
                if (ChunkedWrites)
                {
                    var half = json.Length / 2;
                    Append(json[..half]);
                    await Task.Delay(15, cancellationToken);
                    Append(json[half..]);
                }
                else
                {
                    Append(json);
                }

                Append("\n");
            }
            finally
            {
                Interlocked.Decrement(ref _activeWrites);
            }

            lock (_lock)
            {
                _sent.Add(message);
                _events.Add($"send:{message.Type}");
            }

            if (EchoControlResponses && message.Type == "control_response")
            {
                _inbound.Writer.TryWrite(message);
            }

            void Append(string text)
            {
                lock (_lock)
                {
                    _written.Append(text);
                }
            }
        }

        public async IAsyncEnumerable<CliMessage> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var message in _inbound.Reader.ReadAllAsync(cancellationToken))
            {
                lock (_lock)
                {
                    _received.Add(message);
                }

                yield return message;
            }
        }

        public Task InterruptAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            lock (_lock)
            {
                _events.Add("dispose");
            }

            IsConnected = false;
            _inbound.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
