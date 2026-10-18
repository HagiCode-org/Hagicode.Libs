using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using GitHub.Copilot;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// The gateway tests spawn no processes, but the environment lease coordinator is shared process state.
[NonParallelizable]
public sealed class CopilotUserInputTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string RealCopilotUserInputTestsEnvironmentVariable = "HAGICODE_REAL_CLI_COPILOT_USER_INPUT_TESTS";

    [Test]
    public void ToSdkResponse_accepts_an_offered_choice_without_freeform()
    {
        var response = Answer("Pick", "blue");

        var sdkResponse = response.ToSdkResponse(Question("Pick", ["red", "blue"], allowFreeform: false));

        sdkResponse.ShouldNotBeNull();
        sdkResponse.Answer.ShouldBe("blue");
        sdkResponse.WasFreeform.ShouldBeFalse();
    }

    [Test]
    public void ToSdkResponse_accepts_free_text_when_allowed()
    {
        var response = Answer("Pick", "teal");

        var sdkResponse = response.ToSdkResponse(Question("Pick", ["red", "blue"], allowFreeform: true));

        sdkResponse.ShouldNotBeNull();
        sdkResponse.Answer.ShouldBe("teal");
        sdkResponse.WasFreeform.ShouldBeTrue();
    }

    [Test]
    public void ToSdkResponse_refuses_free_text_when_not_allowed()
    {
        var response = Answer("Pick", "teal");

        response.ToSdkResponse(Question("Pick", ["red", "blue"], allowFreeform: false)).ShouldBeNull();
    }

    [Test]
    public void ToSdkResponse_refuses_several_values()
    {
        var response = new CopilotUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
        {
            ["Pick"] = ["red", "blue"]
        });

        response.ToSdkResponse(Question("Pick", ["red", "blue"], allowFreeform: true)).ShouldBeNull();
    }

    [Test]
    public void ToSdkResponse_refuses_empty_or_missing_values()
    {
        var request = Question("Pick", ["red", "blue"], allowFreeform: true);

        Answer("Pick", "").ToSdkResponse(request).ShouldBeNull();
        Answer("Pick", "   ").ToSdkResponse(request).ShouldBeNull();
        Answer("Other", "red").ToSdkResponse(request).ShouldBeNull();
        new CopilotUserInputResponse(new Dictionary<string, IReadOnlyList<string>>()).ToSdkResponse(request).ShouldBeNull();
    }

    [Test]
    public void ToSdkResponse_accepts_any_value_when_the_question_has_no_choices()
    {
        var sdkResponse = Answer("Name", "Ada").ToSdkResponse(Question("Name", [], allowFreeform: false));

        sdkResponse.ShouldNotBeNull();
        sdkResponse.Answer.ShouldBe("Ada");
        sdkResponse.WasFreeform.ShouldBeTrue();
    }

    [Test]
    public void TryFromCliMessage_reads_a_question_message()
    {
        var message = new CliMessage(
            CopilotUserInputRequest.MessageType,
            JsonSerializer.SerializeToElement(new
            {
                type = CopilotUserInputRequest.MessageType,
                request_id = "tool-call-1",
                question = "Which one?",
                choices = new[] { "A", "B" },
                allow_freeform = true
            }));

        CopilotUserInputRequest.TryFromCliMessage(message, out var request).ShouldBeTrue();

        request.RequestId.ShouldBe("tool-call-1");
        request.Question.ShouldBe("Which one?");
        request.Choices.ShouldBe(["A", "B"]);
        request.AllowFreeform.ShouldBeTrue();
    }

    [Test]
    public void TryFromCliMessage_rejects_other_messages()
    {
        var message = new CliMessage("assistant", JsonSerializer.SerializeToElement(new { text = "hello" }));

        CopilotUserInputRequest.TryFromCliMessage(message, out _).ShouldBeFalse();
    }

    [Test]
    public void Options_default_question_timeout_is_thirty_minutes()
    {
        new CopilotOptions().QuestionTimeout.ShouldBe(TimeSpan.FromMinutes(30));
        CopilotOptions.DefaultQuestionTimeout.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Test]
    public void BuildSdkRequest_accepts_an_infinite_question_timeout()
    {
        var request = CreateProviderForBuild().BuildSdkRequest(
            new CopilotOptions
            {
                QuestionTimeout = Timeout.InfiniteTimeSpan,
                UserInputHandler = NoAnswerHandler
            },
            "hello",
            "/custom/copilot",
            new Dictionary<string, string?>());

        request.QuestionTimeout.ShouldBe(Timeout.InfiniteTimeSpan);
        request.UserInputHandler.ShouldNotBeNull();
    }

    [Test]
    public void BuildSdkRequest_rejects_zero_and_negative_question_timeouts_before_any_sdk_call()
    {
        var provider = CreateProviderForBuild();

        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-5) })
        {
            var exception = Should.Throw<InvalidOperationException>(() => provider.BuildSdkRequest(
                new CopilotOptions { QuestionTimeout = timeout, UserInputHandler = NoAnswerHandler },
                "hello",
                "/custom/copilot",
                new Dictionary<string, string?>()));

            exception.Message.ShouldContain("QuestionTimeout");
        }
    }

    [Test]
    public void CopilotOptions_ToString_does_not_expose_the_github_token()
    {
        var text = new CopilotOptions
        {
            AuthSource = CopilotAuthSource.GitHubToken,
            GitHubToken = "ghu_do_not_print",
            UserInputHandler = NoAnswerHandler
        }.ToString();

        text.ShouldNotContain("ghu_do_not_print");
        text.ShouldContain("UserInputHandler = set");
    }

    [Test]
    public async Task Pending_question_takes_the_tool_call_id_and_closes_in_order()
    {
        var bridge = CreateBridge(async (request, _) => Answer(request.Question, "blue"), out var stream);
        var sdkRequest = Requested("request-1", "tool-1", "Pick a color", ["red", "blue"]);

        bridge.RecordRequested(sdkRequest);
        var response = await bridge.HandleAsync(Input("Pick a color", ["red", "blue"], allowFreeform: false), Invocation("session-1"));

        response.Answer.ShouldBe("blue");
        response.WasFreeform.ShouldBeFalse();
        var message = await ReadNextAsync(stream);
        message.Type.ShouldBe(CopilotSdkStreamEventType.QuestionRequested);
        message.QuestionId.ShouldBe("tool-1");
        message.Choices.ShouldBe(["red", "blue"]);
        bridge.PendingCount.ShouldBe(0);
        bridge.HasDeclinedQuestion.ShouldBeFalse();
    }

    [Test]
    public async Task Two_questions_in_a_row_are_separate_and_the_second_waits_for_the_first()
    {
        var bridge = CreateBridge(async (request, _) => Answer(request.Question, "yes"), out var stream);

        bridge.RecordRequested(Requested("request-1", "tool-1", "First?", ["yes", "no"]));
        await bridge.HandleAsync(Input("First?", ["yes", "no"], allowFreeform: false), Invocation("session-1"));
        bridge.RecordRequested(Requested("request-2", "tool-2", "Second?", ["yes", "no"]));
        await bridge.HandleAsync(Input("Second?", ["yes", "no"], allowFreeform: false), Invocation("session-1"));

        (await ReadNextAsync(stream)).QuestionId.ShouldBe("tool-1");
        (await ReadNextAsync(stream)).QuestionId.ShouldBe("tool-2");
    }

    [Test]
    public async Task Handler_exception_declines_the_question_and_records_a_diagnostic()
    {
        var bridge = CreateBridge((_, _) => throw new InvalidOperationException("boom"), out var stream);
        bridge.RecordRequested(Requested("request-1", "tool-1", "Pick?", ["a"]));

        await Should.ThrowAsync<CopilotUserInputDeclinedException>(
            async () => await bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1")));

        bridge.HasDeclinedQuestion.ShouldBeTrue();
        bridge.PendingCount.ShouldBe(0);
        (await ReadNextAsync(stream)).QuestionId.ShouldBe("tool-1");
        var diagnostic = await ReadNextAsync(stream);
        diagnostic.Type.ShouldBe(CopilotSdkStreamEventType.Diagnostic);
        diagnostic.Content.ShouldContain("tool-1");
        diagnostic.Content.ShouldContain("boom");
    }

    [Test]
    public async Task Null_answer_declines_the_question()
    {
        var bridge = CreateBridge((_, _) => Task.FromResult<CopilotUserInputResponse?>(null), out _);

        await Should.ThrowAsync<CopilotUserInputDeclinedException>(
            async () => await bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1")));

        bridge.HasDeclinedQuestion.ShouldBeTrue();
    }

    [Test]
    public async Task Refused_answer_declines_the_question()
    {
        var bridge = CreateBridge((request, _) => Task.FromResult<CopilotUserInputResponse?>(Answer(request.Question, "other")), out _);

        await Should.ThrowAsync<CopilotUserInputDeclinedException>(
            async () => await bridge.HandleAsync(Input("Pick?", ["a", "b"], allowFreeform: false), Invocation("session-1")));

        bridge.HasDeclinedQuestion.ShouldBeTrue();
    }

    [Test]
    public async Task Question_timeout_declines_even_when_the_handler_ignores_its_token()
    {
        var bridge = CreateBridge(
            async (_, _) =>
            {
                await Task.Delay(TimeSpan.FromMinutes(10));
                return null;
            },
            out _,
            questionTimeout: TimeSpan.FromMilliseconds(150));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await Should.ThrowAsync<CopilotUserInputDeclinedException>(
            async () => await bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1")));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        bridge.HasDeclinedQuestion.ShouldBeTrue();
        bridge.PendingCount.ShouldBe(0);
    }

    [Test]
    public async Task Caller_cancellation_cancels_the_question_without_recording_a_decline()
    {
        using var caller = new CancellationTokenSource();
        var handlerSawCancel = new TaskCompletionSource<bool>();
        var bridge = CreateBridge(
            async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    handlerSawCancel.TrySetResult(token.IsCancellationRequested);
                }

                return null;
            },
            out _,
            callerToken: caller.Token);

        var pending = bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1"));
        await caller.CancelAsync();

        await Should.ThrowAsync<CopilotUserInputDeclinedException>(async () => await pending);
        (await handlerSawCancel.Task).ShouldBeTrue();
        bridge.HasDeclinedQuestion.ShouldBeFalse();
    }

    [Test]
    public async Task Event_arriving_after_an_unmatched_callback_started_is_dropped_and_keeps_the_generated_id()
    {
        var gate = new TaskCompletionSource<bool>();
        var bridge = CreateBridge(
            async (request, _) =>
            {
                await gate.Task;
                return Answer(request.Question, "a");
            },
            out var stream);

        var pending = bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-9"));
        bridge.RecordRequested(Requested("late-request", "late-tool", "Pick?", ["a"]));
        bridge.RecordCompleted("late-request").ShouldBeNull();
        gate.SetResult(true);
        (await pending).Answer.ShouldBe("a");

        (await ReadNextAsync(stream)).QuestionId.ShouldBe("copilot-input-session-9-1");
        bridge.RecordCompleted("late-request").ShouldBeNull();
        bridge.PendingCount.ShouldBe(0);
    }

    [Test]
    public async Task Unmatched_event_stays_queued_for_a_later_question_with_the_same_text()
    {
        var bridge = CreateBridge(async (request, _) => Answer(request.Question, "a"), out _);

        bridge.RecordRequested(Requested("request-other", "tool-other", "Other?", ["a"]));
        var unrelated = await bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1"));
        unrelated.Answer.ShouldBe("a");

        var matched = await bridge.HandleAsync(Input("Other?", ["a"], allowFreeform: false), Invocation("session-1"));
        matched.Answer.ShouldBe("a");
    }

    [Test]
    public async Task Completed_before_the_callback_removes_the_queued_question()
    {
        var bridge = CreateBridge(async (request, _) => Answer(request.Question, "a"), out var stream);

        bridge.RecordRequested(Requested("request-1", "tool-1", "Pick?", ["a"]));
        bridge.RecordCompleted("request-1").ShouldBeNull();

        await bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1"));

        (await ReadNextAsync(stream)).QuestionId.ShouldStartWith("copilot-input-session-1-");
    }

    [Test]
    public async Task Completed_for_a_pending_question_cancels_it_and_emits_a_closing_event()
    {
        var handlerCancelled = new TaskCompletionSource<bool>();
        var bridge = CreateBridge(
            async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    handlerCancelled.TrySetResult(token.IsCancellationRequested);
                }

                return null;
            },
            out var stream);
        bridge.RecordRequested(Requested("request-1", "tool-1", "Pick?", ["a"]));

        var pending = bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1"));
        (await ReadNextAsync(stream)).QuestionId.ShouldBe("tool-1");

        var closing = bridge.RecordCompleted("request-1");

        closing.ShouldNotBeNull();
        closing.Type.ShouldBe(CopilotSdkStreamEventType.QuestionClosed);
        closing.QuestionId.ShouldBe("tool-1");
        (await handlerCancelled.Task).ShouldBeTrue();
        await Should.ThrowAsync<CopilotUserInputDeclinedException>(async () => await pending);
        bridge.HasDeclinedQuestion.ShouldBeFalse();
    }

    [Test]
    public async Task Completed_after_an_answer_is_ignored()
    {
        var bridge = CreateBridge(async (request, _) => Answer(request.Question, "a"), out _);
        bridge.RecordRequested(Requested("request-1", "tool-1", "Pick?", ["a"]));
        await bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1"));

        bridge.RecordCompleted("request-1").ShouldBeNull();
    }

    [Test]
    public async Task Pending_questions_report_paused_time_and_notify_progress_when_the_last_one_closes()
    {
        var progress = 0;
        var answered = new TaskCompletionSource<bool>();
        var bridge = CreateBridge(
            async (request, _) =>
            {
                await answered.Task;
                return Answer(request.Question, "a");
            },
            out _,
            recordProgress: () => Interlocked.Increment(ref progress));

        var pending = bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1"));
        bridge.PendingCount.ShouldBe(1);
        await Task.Delay(120);
        answered.SetResult(true);
        await pending;

        bridge.PendingCount.ShouldBe(0);
        bridge.PausedDuration.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(100));
        progress.ShouldBe(1);
    }

    [Test]
    public void DispatchSessionEvent_without_a_bridge_keeps_the_raw_fallback()
    {
        var evt = new UserInputRequestedEvent
        {
            Data = new UserInputRequestedData
            {
                RequestId = "request-1",
                ToolCallId = "tool-1",
                Question = "Pick?",
                Choices = ["a"],
                AllowFreeform = false
            }
        };

        var result = GitHubCopilotSdkGateway.DispatchSessionEvent(evt, sawDelta: false);

        result.Events.Count.ShouldBe(1);
        result.Events[0].Type.ShouldBe(CopilotSdkStreamEventType.RawEvent);
        result.Events[0].RawEventType.ShouldBe(nameof(UserInputRequestedEvent));
    }

    [Test]
    public async Task DispatchSessionEvent_with_a_bridge_queues_the_request_and_closes_a_pending_question()
    {
        var bridge = CreateBridge((_, _) => Task.FromResult<CopilotUserInputResponse?>(null), out _);
        var requested = new UserInputRequestedEvent
        {
            Data = new UserInputRequestedData { RequestId = "request-1", ToolCallId = "tool-1", Question = "Pick?", Choices = ["a"], AllowFreeform = false }
        };

        GitHubCopilotSdkGateway.DispatchSessionEvent(requested, sawDelta: false, bridge).Events.ShouldBeEmpty();
        await Should.ThrowAsync<CopilotUserInputDeclinedException>(
            async () => await bridge.HandleAsync(Input("Pick?", ["a"], allowFreeform: false), Invocation("session-1")));

        var completed = new UserInputCompletedEvent
        {
            Data = new UserInputCompletedData { RequestId = "request-1", Answer = "a", WasFreeform = false }
        };
        GitHubCopilotSdkGateway.DispatchSessionEvent(completed, sawDelta: false, bridge).Events.ShouldBeEmpty();
    }

    [Test]
    public void Permission_handler_is_unchanged_and_never_the_question_bridge()
    {
        var factory = new ScriptedSdkClientFactory(static (_, _) => Task.CompletedTask);
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));

        _ = gateway.CreateRuntimeAsync(CreateSdkRequest(NoAnswerHandler)).Result;
        _ = gateway.CreateRuntimeAsync(CreateSdkRequest(handler: null)).Result;

        var withHandler = factory.SessionConfigs[0];
        var withoutHandler = factory.SessionConfigs[1];
        withHandler.OnUserInputRequest.ShouldNotBeNull();
        withoutHandler.OnUserInputRequest.ShouldBeNull();
        withHandler.OnPermissionRequest.ShouldBe(withoutHandler.OnPermissionRequest);
        withHandler.Hooks.ShouldNotBeNull();
    }

    [Test]
    public async Task Fresh_resumed_and_resume_failed_sessions_all_register_the_callback()
    {
        var factory = new ScriptedSdkClientFactory(static (_, _) => Task.CompletedTask);
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));

        await gateway.CreateRuntimeAsync(CreateSdkRequest(NoAnswerHandler));
        await gateway.CreateRuntimeAsync(CreateSdkRequest(NoAnswerHandler, sessionId: "resume-1"));
        factory.ResumeFailure = new InvalidOperationException("Request session.resume failed with message: boom");
        // Resume retries back off for six seconds before the fallback, so the startup bound is raised for this request.
        await gateway.CreateRuntimeAsync(CreateSdkRequest(NoAnswerHandler, sessionId: "resume-2", startupTimeout: TimeSpan.FromSeconds(30)));

        factory.SessionConfigs.Count.ShouldBe(2);
        factory.SessionConfigs.ShouldAllBe(config => config.OnUserInputRequest != null);
        factory.ResumeConfigs.Count.ShouldBe(4);
        factory.ResumeConfigs.Take(2).ShouldAllBe(config => config.OnUserInputRequest != null);
    }

    [Test]
    public void Gateway_without_a_handler_registers_no_callback_on_resume()
    {
        var factory = new ScriptedSdkClientFactory(static (_, _) => Task.CompletedTask);
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));

        _ = gateway.CreateRuntimeAsync(CreateSdkRequest(handler: null, sessionId: "resume-1")).Result;

        factory.ResumeConfigs.Single().OnUserInputRequest.ShouldBeNull();
    }

    [Test]
    public async Task Slow_answer_past_the_idle_and_request_timeouts_succeeds()
    {
        var factory = new ScriptedSdkClientFactory(async (session, _) =>
        {
            await session.AskAsync("Pick?", ["a", "b"], allowFreeform: false, requestId: "request-1", toolCallId: "tool-1");
            session.Emit(TextEvent("done"));
        });
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));
        var request = CreateSdkRequest(
            async (question, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1200));
                return Answer(question.Question, "a");
            },
            timeout: TimeSpan.FromMilliseconds(400),
            idleTimeout: TimeSpan.FromMilliseconds(300));

        var events = await CollectAsync(gateway, request);

        events.ShouldNotContain(static evt => evt.Type == CopilotSdkStreamEventType.Error);
        events.Last().Type.ShouldBe(CopilotSdkStreamEventType.Completed);
        events.ShouldContain(static evt => evt.Type == CopilotSdkStreamEventType.TextDelta && evt.Content == "done");
    }

    [Test]
    public async Task Stall_after_the_answer_still_ends_with_the_idle_timeout()
    {
        var factory = new ScriptedSdkClientFactory(async (session, token) =>
        {
            await session.AskAsync("Pick?", ["a"], allowFreeform: false, requestId: "request-1", toolCallId: "tool-1");
            await Task.Delay(Timeout.Infinite, token);
        });
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));
        var request = CreateSdkRequest(
            (question, _) => Task.FromResult<CopilotUserInputResponse?>(Answer(question.Question, "a")),
            timeout: TimeSpan.FromSeconds(30),
            idleTimeout: TimeSpan.FromMilliseconds(300));

        var events = await CollectAsync(gateway, request);

        var error = events.Last();
        error.Type.ShouldBe(CopilotSdkStreamEventType.Error);
        error.ErrorMessage.ShouldContain("[idle_timeout]");
    }

    [Test]
    public async Task Request_deadline_still_applies_when_no_question_is_pending()
    {
        var factory = new ScriptedSdkClientFactory(static (_, token) => Task.Delay(Timeout.Infinite, token));
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));
        var request = CreateSdkRequest(
            NoAnswerHandler,
            timeout: TimeSpan.FromMilliseconds(300),
            idleTimeout: TimeSpan.FromSeconds(30));

        var events = await CollectAsync(gateway, request);

        var error = events.Last();
        error.Type.ShouldBe(CopilotSdkStreamEventType.Error);
        error.ErrorMessage.ShouldContain("[request_timeout]");
    }

    [Test]
    public async Task Question_timeout_declines_and_the_run_continues_to_its_own_result()
    {
        var factory = new ScriptedSdkClientFactory(async (session, _) =>
        {
            await session.AskAsync("Pick?", ["a"], allowFreeform: false, requestId: "request-1", toolCallId: "tool-1");
            session.Emit(TextEvent("continued"));
        });
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));
        var request = CreateSdkRequest(
            static async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
            questionTimeout: TimeSpan.FromMilliseconds(200));

        var events = await CollectAsync(gateway, request);

        events.ShouldNotContain(static evt => evt.Type == CopilotSdkStreamEventType.Error);
        events.ShouldContain(static evt => evt.Type == CopilotSdkStreamEventType.TextDelta && evt.Content == "continued");
        events.Last().Type.ShouldBe(CopilotSdkStreamEventType.Completed);
    }

    [Test]
    public async Task Completed_event_while_pending_emits_a_closing_event_in_stream_order()
    {
        var factory = new ScriptedSdkClientFactory(async (session, _) =>
        {
            var asked = session.AskAsync("Pick?", ["a"], allowFreeform: false, requestId: "request-1", toolCallId: "tool-1", completeWhilePending: true);
            await asked;
            session.Emit(TextEvent("continued"));
        });
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));
        var request = CreateSdkRequest(
            static async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            });

        var events = await CollectAsync(gateway, request);

        var questionIndex = events.FindIndex(evt => evt.Type == CopilotSdkStreamEventType.QuestionRequested);
        var closedIndex = events.FindIndex(evt => evt.Type == CopilotSdkStreamEventType.QuestionClosed);
        questionIndex.ShouldBeGreaterThanOrEqualTo(0);
        closedIndex.ShouldBeGreaterThan(questionIndex);
        events[closedIndex].QuestionId.ShouldBe("tool-1");
        events.Last().Type.ShouldBe(CopilotSdkStreamEventType.Completed);
    }

    [Test]
    public async Task Caller_cancellation_ends_the_run_and_cancels_the_pending_question()
    {
        using var caller = new CancellationTokenSource();
        var handlerCancelled = new TaskCompletionSource<bool>();
        var factory = new ScriptedSdkClientFactory(async (session, _) =>
        {
            await session.AskAsync("Pick?", ["a"], allowFreeform: false, requestId: "request-1", toolCallId: "tool-1");
        });
        var gateway = new GitHubCopilotSdkGateway(factory, new CopilotSdkEnvironmentLeaseCoordinator(), TimeSpan.FromMilliseconds(50));
        var request = CreateSdkRequest(
            async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    handlerCancelled.TrySetResult(token.IsCancellationRequested);
                }

                return null;
            });

        var consuming = CollectAsync(gateway, request, caller.Token);
        await Task.Delay(200);
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await consuming);
        (await handlerCancelled.Task).ShouldBeTrue();
    }

    [Test]
    public void BuildCliArgs_omits_no_ask_user_when_a_handler_is_set()
    {
        var result = CopilotCliCompatibility.BuildCliArgs(new CopilotOptions
        {
            NoAskUser = true,
            UserInputHandler = NoAnswerHandler
        });

        result.CliArgs.ShouldNotContain("--no-ask-user");
        result.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public void BuildCliArgs_drops_configured_no_ask_user_with_a_diagnostic_when_a_handler_is_set()
    {
        var result = CopilotCliCompatibility.BuildCliArgs(new CopilotOptions
        {
            NoAskUser = false,
            AdditionalArgs = ["--no-ask-user", "--log-level", "debug"],
            UserInputHandler = NoAnswerHandler
        });

        result.CliArgs.ShouldNotContain("--no-ask-user");
        result.CliArgs.ShouldContain("--log-level");
        result.Diagnostics.ShouldHaveSingleItem();
        result.Diagnostics[0].ShouldContain("--no-ask-user");
        result.Diagnostics[0].ShouldContain("user input is handled");
    }

    [Test]
    public void BuildCliArgs_keeps_no_ask_user_once_without_a_handler()
    {
        var derived = CopilotCliCompatibility.BuildCliArgs(new CopilotOptions { NoAskUser = true });
        var duplicated = CopilotCliCompatibility.BuildCliArgs(new CopilotOptions
        {
            NoAskUser = true,
            AdditionalArgs = ["--no-ask-user"]
        });

        derived.CliArgs.Count(static arg => arg == "--no-ask-user").ShouldBe(1);
        duplicated.CliArgs.Count(static arg => arg == "--no-ask-user").ShouldBe(1);
        duplicated.Diagnostics.ShouldBeEmpty();
    }

    [Test]
    public async Task Provider_maps_question_events_in_stream_order_around_text_tools_and_results()
    {
        var events = new List<CopilotSdkStreamEvent>
        {
            new(CopilotSdkStreamEventType.SessionStarted, SessionId: "session-1"),
            new(CopilotSdkStreamEventType.AssistantSnapshot, SessionId: "session-1", Content: "Before"),
            new(CopilotSdkStreamEventType.QuestionRequested, SessionId: "session-1", QuestionId: "tool-1", Question: "Pick?", Choices: ["a", "b"], AllowFreeform: true),
            new(CopilotSdkStreamEventType.QuestionClosed, SessionId: "session-1", QuestionId: "tool-1"),
            new(CopilotSdkStreamEventType.ToolExecutionStart, SessionId: "session-1", ToolName: "read", ToolCallId: "tool-2"),
            new(CopilotSdkStreamEventType.Completed, SessionId: "session-1")
        };
        var provider = new CopilotProvider(new FixedExecutableResolver(), new CliProcessManager(), new EventListGateway(events));

        var messages = await CollectMessagesAsync(provider, new CopilotOptions { ExecutablePath = "/custom/copilot" });

        messages.Select(static message => message.Type).ShouldBe(
            ["session.started", "assistant", "question.request", "question.closed", "tool.started", "result"]);
        var question = messages[2].Content;
        question.GetProperty("request_id").GetString().ShouldBe("tool-1");
        question.GetProperty("question").GetString().ShouldBe("Pick?");
        question.GetProperty("choices").EnumerateArray().Select(static item => item.GetString()).ShouldBe(["a", "b"]);
        question.GetProperty("allow_freeform").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task Provider_without_question_events_emits_the_same_sequence_as_before()
    {
        var events = new List<CopilotSdkStreamEvent>
        {
            new(CopilotSdkStreamEventType.SessionStarted, SessionId: "session-1"),
            new(CopilotSdkStreamEventType.TextDelta, SessionId: "session-1", Content: "Hello"),
            new(CopilotSdkStreamEventType.Completed, SessionId: "session-1")
        };
        var provider = new CopilotProvider(new FixedExecutableResolver(), new CliProcessManager(), new EventListGateway(events));

        var messages = await CollectMessagesAsync(provider, new CopilotOptions { ExecutablePath = "/custom/copilot" });

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "result"]);
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ExecuteAsync_real_cli_asks_and_answers_one_question_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);
        ExternalGate.Require(RealCopilotUserInputTestsEnvironmentVariable);

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(["copilot"]);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Copilot CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var asked = new ConcurrentQueue<CopilotUserInputRequest>();
        var provider = new CopilotProvider(resolver, new CliProcessManager());
        var messages = await CollectMessagesAsync(provider, new CopilotOptions
        {
            ExecutablePath = executablePath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Timeout = TimeSpan.FromMinutes(3),
            StartupTimeout = TimeSpan.FromSeconds(30),
            UserInputHandler = (question, _) =>
            {
                asked.Enqueue(question);
                return Task.FromResult<CopilotUserInputResponse?>(Answer(question.Question, question.Choices[0]));
            }
        }, "Use the ask_user tool to ask me one question with exactly two choices, red and blue. Then reply with the color I picked.");

        asked.ShouldNotBeEmpty();
        messages.Select(static message => message.Type).ShouldContain(CopilotUserInputRequest.MessageType);
        messages.Last().Type.ShouldBe("result");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ExecuteAsync_real_cli_declines_one_question_and_completes_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);
        ExternalGate.Require(RealCopilotUserInputTestsEnvironmentVariable);

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(["copilot"]);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Copilot CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var provider = new CopilotProvider(resolver, new CliProcessManager());
        var messages = await CollectMessagesAsync(provider, new CopilotOptions
        {
            ExecutablePath = executablePath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Timeout = TimeSpan.FromMinutes(3),
            StartupTimeout = TimeSpan.FromSeconds(30),
            UserInputHandler = static (_, _) => Task.FromResult<CopilotUserInputResponse?>(null)
        }, "Use the ask_user tool to ask me one question with exactly two choices, red and blue. If I do not answer, reply with the word 'declined'.");

        messages.Select(static message => message.Type).ShouldContain(CopilotUserInputRequest.MessageType);
        messages.Last().Type.ShouldBe("result");
    }

    // ---- helpers -------------------------------------------------------------------------------------------------

    private static readonly Func<CopilotUserInputRequest, CancellationToken, Task<CopilotUserInputResponse?>> NoAnswerHandler =
        static (_, _) => Task.FromResult<CopilotUserInputResponse?>(null);

    private static CopilotUserInputResponse Answer(string question, string value)
    {
        return new CopilotUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
        {
            [question] = [value]
        });
    }

    private static UserInputRequest Input(string question, string[] choices, bool allowFreeform)
    {
        return new UserInputRequest
        {
            Question = question,
            Choices = choices,
            AllowFreeform = allowFreeform
        };
    }

    private static CopilotUserInputRequest Question(string question, string[] choices, bool allowFreeform)
    {
        return new CopilotUserInputRequest("question-1", question, choices, allowFreeform);
    }

    private static UserInputInvocation Invocation(string sessionId)
    {
        return new UserInputInvocation { SessionId = sessionId };
    }

    private static UserInputRequestedData Requested(string requestId, string toolCallId, string question, string[] choices)
    {
        return new UserInputRequestedData
        {
            RequestId = requestId,
            ToolCallId = toolCallId,
            Question = question,
            Choices = choices,
            AllowFreeform = false
        };
    }

    private static CopilotPendingQuestions CreateBridge(
        Func<CopilotUserInputRequest, CancellationToken, Task<CopilotUserInputResponse?>> handler,
        out ChannelReader<CopilotSdkStreamEvent> stream,
        TimeSpan? questionTimeout = null,
        CancellationToken callerToken = default,
        Action? recordProgress = null)
    {
        var channel = Channel.CreateUnbounded<CopilotSdkStreamEvent>();
        var bridge = new CopilotPendingQuestions(handler, questionTimeout ?? TimeSpan.FromMinutes(30), callerToken);
        bridge.Attach(channel.Writer, recordProgress ?? (() => { }));
        stream = channel.Reader;
        return bridge;
    }

    private static async Task<CopilotSdkStreamEvent> ReadNextAsync(ChannelReader<CopilotSdkStreamEvent> reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        return await reader.ReadAsync(timeout.Token);
    }

    private static CopilotSdkRequest CreateSdkRequest(
        Func<CopilotUserInputRequest, CancellationToken, Task<CopilotUserInputResponse?>>? handler,
        TimeSpan? timeout = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? questionTimeout = null,
        string? sessionId = null,
        TimeSpan? startupTimeout = null)
    {
        return new CopilotSdkRequest(
            Prompt: "continue",
            Model: null,
            WorkingDirectory: "/tmp/project",
            SessionId: sessionId,
            CliPath: "/custom/copilot",
            CliUrl: null,
            GitHubToken: null,
            UseLoggedInUser: true,
            Timeout: timeout ?? TimeSpan.FromSeconds(30),
            IdleTimeout: idleTimeout ?? TimeSpan.FromSeconds(30),
            StartupTimeout: startupTimeout ?? TimeSpan.FromSeconds(5),
            CliArgs: [],
            EnvironmentVariables: new Dictionary<string, string?>(),
            UserInputHandler: handler,
            QuestionTimeout: questionTimeout ?? TimeSpan.FromMinutes(30));
    }

    private static async Task<List<CopilotSdkStreamEvent>> CollectAsync(
        ICopilotSdkGateway gateway,
        CopilotSdkRequest request,
        CancellationToken cancellationToken = default)
    {
        var events = new List<CopilotSdkStreamEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        await foreach (var evt in gateway.SendPromptAsync(request, linked.Token))
        {
            events.Add(evt);
            if (evt.Type is CopilotSdkStreamEventType.Completed or CopilotSdkStreamEventType.Error)
            {
                break;
            }
        }

        return events;
    }

    private static async Task<List<CliMessage>> CollectMessagesAsync(
        ICliProvider<CopilotOptions> provider,
        CopilotOptions options,
        string prompt = "hello")
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static CopilotProvider CreateProviderForBuild()
    {
        return new CopilotProvider(new CliExecutableResolver(), new CliProcessManager());
    }

    private static SessionEvent TextEvent(string text)
    {
        return new AssistantMessageDeltaEvent
        {
            Data = new AssistantMessageDeltaData
            {
                MessageId = "message-1",
                DeltaContent = text
            }
        };
    }

    private sealed class FixedExecutableResolver : CliExecutableResolver
    {
        public override IReadOnlyList<string> ResolveExecutablePaths(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => string.IsNullOrWhiteSpace(executableName) ? [] : [executableName];

        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class EventListGateway(IReadOnlyList<CopilotSdkStreamEvent> events) : ICopilotSdkGateway
    {
        public Task<ICopilotSdkRuntime> CreateRuntimeAsync(CopilotSdkRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<CopilotSdkStreamEvent> SendPromptAsync(
            CopilotSdkRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var evt in events)
            {
                yield return evt;
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// A fake SDK client that records the configuration of each session and runs a script for each prompt. The
    /// <c>ask_user</c> callback is invoked the way the SDK does for a question; an exception from the callback is reported to
    /// the script as a failed tool call, not a session error, because the SDK behavior for a decline is gate G2.
    /// </summary>
    private sealed class ScriptedSdkClientFactory(Func<ScriptedSession, CancellationToken, Task> script) : ICopilotSdkClientFactory
    {
        private int _sessionCounter;

        public Func<ScriptedSession, CancellationToken, Task> Script { get; } = script;

        public List<SessionConfig> SessionConfigs { get; } = [];

        public List<ResumeSessionConfig> ResumeConfigs { get; } = [];

        public Exception? ResumeFailure { get; set; }

        public ICopilotSdkClient Create(CopilotClientOptions options)
        {
            return new ScriptedClient(this);
        }

        private sealed class ScriptedClient(ScriptedSdkClientFactory owner) : ICopilotSdkClient
        {
            public Task<ICopilotSdkSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
            {
                owner.SessionConfigs.Add(config);
                var sessionId = config.SessionId ?? $"scripted-session-{Interlocked.Increment(ref owner._sessionCounter)}";
                return Task.FromResult<ICopilotSdkSession>(new ScriptedSession(sessionId, owner.Script, config.OnUserInputRequest));
            }

            public Task<ICopilotSdkSession> ResumeSessionAsync(string sessionId, ResumeSessionConfig config, CancellationToken cancellationToken)
            {
                owner.ResumeConfigs.Add(config);
                if (owner.ResumeFailure is { } failure)
                {
                    return Task.FromException<ICopilotSdkSession>(failure);
                }

                return Task.FromResult<ICopilotSdkSession>(new ScriptedSession(sessionId, owner.Script, config.OnUserInputRequest));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ScriptedSession(
        string sessionId,
        Func<ScriptedSession, CancellationToken, Task> script,
        Func<UserInputRequest, UserInputInvocation, Task<UserInputResponse>>? userInput) : ICopilotSdkSession
    {
        private event Action<SessionEvent>? SessionEventReceived;

        public string SessionId => sessionId;

        public IDisposable On(Action<SessionEvent> handler)
        {
            SessionEventReceived += handler;
            return new DelegateDisposable(() => SessionEventReceived -= handler);
        }

        public Task SendAndWaitAsync(MessageOptions options, TimeSpan timeout, CancellationToken cancellationToken)
        {
            return script(this, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>
        /// Emits <c>user_input.requested</c>, starts the callback, and emits <c>user_input.completed</c> while the callback is
        /// pending when <paramref name="completeWhilePending" /> is set. Returns the answer, or <see langword="null" /> when the
        /// callback failed.
        /// </summary>
        public async Task<UserInputResponse?> AskAsync(
            string question,
            string[] choices,
            bool allowFreeform,
            string requestId,
            string toolCallId,
            bool completeWhilePending = false)
        {
            var requested = new UserInputRequestedEvent
            {
                Data = new UserInputRequestedData
                {
                    RequestId = requestId,
                    ToolCallId = toolCallId,
                    Question = question,
                    Choices = choices,
                    AllowFreeform = allowFreeform
                }
            };

            if (userInput is null)
            {
                throw new InvalidOperationException("The session has no user-input callback.");
            }

            Emit(requested);
            var callback = userInput(
                new UserInputRequest { Question = question, Choices = choices, AllowFreeform = allowFreeform },
                new UserInputInvocation { SessionId = sessionId });
            if (completeWhilePending)
            {
                Emit(new UserInputCompletedEvent { Data = new UserInputCompletedData { RequestId = requestId, Answer = "elsewhere", WasFreeform = false } });
            }

            try
            {
                return await callback;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Emit(SessionEvent evt)
        {
            SessionEventReceived?.Invoke(evt);
        }

        private sealed class DelegateDisposable(Action disposeAction) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    disposeAction();
                }
            }
        }
    }
}
