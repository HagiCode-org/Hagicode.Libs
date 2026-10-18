using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Providers.OpenCode;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Pins the OpenCode 2.0 wire contract of <see cref="OpenCodeHttpClient"/> with a scripted handler (no server).
/// </summary>
public sealed class OpenCodeHttpClientTests
{
    private const string Password = "stub-secret";
    private const string SessionId = "ses_1";

    [Test]
    public async Task Requests_use_api_routes_with_basic_credentials_and_send_the_directory_in_the_body()
    {
        var handler = new ScriptedHandler(request => request.Method == "POST"
            ? Json($"{{\"data\":{{\"id\":\"{SessionId}\",\"location\":{{\"directory\":\"/work/project\"}}}}}}")
            : Json("{\"version\":\"2.0.32\",\"pid\":1,\"urls\":[],\"paths\":{\"tmp\":\"/tmp\"}}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, "/work/project", "legacy-workspace", Password);

        var health = await client.HealthAsync();
        var session = await client.CreateSessionAsync("title");

        health.Healthy.ShouldBeTrue();
        session.Location!.Directory.ShouldBe("/work/project");
        handler.Requests.Select(static request => request.PathAndQuery).ShouldBe(["/api/info", "/api/session"]);
        handler.Requests.All(static request => request.Authorization == ExpectedBasic()).ShouldBeTrue();
        handler.Requests[1].Body.ShouldContain("\"location\":{\"directory\":\"/work/project\"}");
        handler.Requests[1].Body.ShouldNotContain("workspace");
        handler.Requests[1].Body.ShouldNotContain("legacy-workspace");
    }

    [Test]
    public async Task Requests_carry_no_credential_when_no_password_is_configured()
    {
        var handler = new ScriptedHandler(static _ => Json("{\"version\":\"2.0.32\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null);

        await client.HealthAsync();

        handler.Requests.Single().Authorization.ShouldBeNull();
    }

    [Test]
    public async Task Unauthorized_response_is_reported_as_an_authentication_failure_without_the_password()
    {
        var handler = new ScriptedHandler(static _ => Status(HttpStatusCode.Unauthorized, "{\"_tag\":\"UnauthorizedError\",\"message\":\"Authentication required\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var exception = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () => await client.HealthAsync());

        exception.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        exception.Message.ShouldContain("authentication failed");
        exception.Message.ShouldNotContain(Password);
    }

    [Test]
    public async Task Unauthorized_response_without_a_configured_password_asks_for_one()
    {
        var handler = new ScriptedHandler(static _ => Status(HttpStatusCode.Unauthorized, "{}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null);

        var exception = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () => await client.HealthAsync());

        exception.Message.ShouldContain("server password is missing or wrong");
        handler.Requests.Single().Authorization.ShouldBeNull();
    }

    [Test]
    public async Task Html_body_with_success_status_is_reported_as_an_unsupported_api()
    {
        var handler = new ScriptedHandler(static _ => Html("<!doctype html><html><body>OpenCode</body></html>"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var exception = await Should.ThrowAsync<OpenCodeUnsupportedApiException>(async () => await client.HealthAsync());

        exception.Message.ShouldContain("/api/info");
        exception.Message.ShouldContain("not the OpenCode 2.0 API");
        exception.ResponseBody.ShouldContain("<!doctype html>");
    }

    [Test]
    public async Task Listing_files_and_sessions_uses_the_2_0_routes()
    {
        var handler = new ScriptedHandler(request => request.PathAndQuery.StartsWith("/api/fs/list", StringComparison.Ordinal)
            ? Json("{\"location\":{\"directory\":\"/work\"},\"data\":[{\"path\":\"src\",\"type\":\"directory\"}]}")
            : Json($"{{\"data\":[{{\"id\":\"{SessionId}\"}}],\"cursor\":{{\"previous\":null,\"next\":null}}}}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: "/work", workspace: null, Password);

        var files = await client.ListFilesAsync(".");
        var sessions = await client.ListSessionsAsync();

        files.Single().Path.ShouldBe("src");
        files.Single().Type.ShouldBe("directory");
        sessions.Single().Id.ShouldBe(SessionId);
        handler.Requests[0].PathAndQuery.ShouldBe("/api/fs/list?path=.&location%5Bdirectory%5D=%2Fwork");
        handler.Requests[1].PathAndQuery.ShouldBe("/api/session");
    }

    [Test]
    public async Task Prompt_assembles_the_assistant_text_from_the_event_stream()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.inbox.enqueued", new { sessionID = SessionId }),
            Event("session.execution.started", new { sessionID = SessionId }),
            Event("session.text.ended", new { sessionID = "ses_other", assistantMessageID = "msg_other", text = "WRONG" }),
            Event("session.text.ended", new { sessionID = SessionId, assistantMessageID = "msg_a", ordinal = 0, text = "Part one" }),
            Event("session.text.ended", new { sessionID = SessionId, assistantMessageID = "msg_a", ordinal = 1, text = "Part two" }),
            Event("session.execution.succeeded", new { sessionID = SessionId }),
            Event("session.usage.updated", new { sessionID = SessionId })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var response = await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        response.GetTextContent().ShouldBe($"Part one{Environment.NewLine}Part two");
        response.MessageId.ShouldBe("msg_a");
        response.Role.ShouldBe("assistant");
        response.SessionId.ShouldBe(SessionId);
    }

    [Test]
    public async Task Prompt_subscribes_to_events_before_posting_the_prompt()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.text.ended", new { sessionID = SessionId, assistantMessageID = "msg_a", text = "pong" }),
            Event("session.execution.succeeded", new { sessionID = SessionId })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        var order = handler.Requests.Select(static request => request.PathAndQuery).ToArray();
        order.ShouldBe(["/api/event", $"/api/session/{SessionId}/prompt"]);
        handler.Requests[1].Body.ShouldContain("\"text\":\"hi\"");
    }

    [Test]
    public async Task Failed_turn_returns_a_diagnostic_envelope_that_names_the_failure()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.execution.failed", new { sessionID = SessionId, error = new { type = "provider.no-route", message = "Model unavailable: a/b" } })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var response = await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        response.GetTextContent().ShouldBeEmpty();
        var summary = response.BuildDiagnosticSummary();
        summary.ShouldContain("failed turn");
        summary.ShouldContain("error.message=Model unavailable: a/b");
        summary.ShouldContain("error.reason=provider.no-route");
    }

    [Test]
    public async Task Successful_turn_without_text_is_distinguishable_from_a_failure()
    {
        var handler = PromptScript(Events(
            Event("server.connected", new { }),
            Event("session.execution.succeeded", new { sessionID = SessionId })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var response = await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"));

        response.GetTextContent().ShouldBeEmpty();
        var summary = response.BuildDiagnosticSummary();
        summary.ShouldContain("successful response with no readable text content");
        summary.ShouldContain("partTypes=(none)");
    }

    [Test]
    public async Task Prompt_with_a_cancelled_token_throws_cancellation()
    {
        var handler = PromptScript(Events(Event("server.connected", new { })));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi"), cancelled.Token));
    }

    [Test]
    public async Task Prompt_that_never_finishes_times_out_with_a_timeout_not_a_protocol_error()
    {
        var handler = new ScriptedHandler(request => request.PathAndQuery == "/api/event"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingEventStream(Encoding.UTF8.GetBytes($"data: {Event("server.connected", new { })}\n\n")))
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream") },
                },
            }
            : Json("{\"data\":{\"id\":\"msg_1\"}}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/"), Timeout = TimeSpan.FromMilliseconds(300) };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var exception = await Should.ThrowAsync<TimeoutException>(async () =>
            await client.PromptAsync(SessionId, OpenCodeSessionPromptRequest.FromText("hi")));

        exception.Message.ShouldContain("did not finish");
    }

    [Test]
    public async Task Form_routes_list_reply_and_cancel_against_the_fixture_with_settled_outcomes()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript
        {
            Forms =
            [
                McpForm("Name", OpenCodeFormKinds.Mcp, type: false),
                McpForm("Pets", OpenCodeFormKinds.Mcp, type: true),
                McpForm("Note", OpenCodeFormKinds.Mcp, type: false),
            ],
        });

        // MCP forms are left to their owner, so the turn completes and the three forms stay pending.
        await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"));
        var pending = await fixture.Client.ListFormsAsync(fixture.SessionId);
        pending.Count.ShouldBe(3);
        pending.ShouldAllBe(static form => form.Kind == OpenCodeFormKinds.Mcp);
        var name = pending.Single(static form => form.Fields[0].Title == "Name");
        var pets = pending.Single(static form => form.Fields[0].Title == "Pets");
        var note = pending.Single(static form => form.Fields[0].Title == "Note");

        (await fixture.Client.ReplyFormAsync(fixture.SessionId, name.Id, Answer(("q0", "Ada")))).ShouldBe(OpenCodeFormOutcome.Accepted);

        // A wrong shape is refused and the form stays pending.
        var refused = await Should.ThrowAsync<OpenCodeApiException>(async () =>
            await fixture.Client.ReplyFormAsync(fixture.SessionId, pets.Id, Answer(("q0", "Cats"))));
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refused.ResponseBody.ShouldContain("Expected string array for form field: q0");
        (await fixture.Client.ReplyFormAsync(fixture.SessionId, pets.Id, Answer(("q0", new[] { "Cats", "Hamsters" }))))
            .ShouldBe(OpenCodeFormOutcome.Accepted);

        (await fixture.Client.CancelFormAsync(fixture.SessionId, note.Id, "Not now.")).ShouldBe(OpenCodeFormOutcome.Accepted);

        (await fixture.Client.ReplyFormAsync(fixture.SessionId, pets.Id, Answer(("q0", new[] { "Cats" }))))
            .ShouldBe(OpenCodeFormOutcome.AlreadySettled);
        (await fixture.Client.CancelFormAsync(fixture.SessionId, note.Id, "again")).ShouldBe(OpenCodeFormOutcome.AlreadySettled);
        (await fixture.Client.ListFormsAsync(fixture.SessionId)).ShouldBeEmpty();

        var forms = fixture.Server.GetForms();
        forms.Single(form => form.Id == name.Id).State.ShouldBe(OpenCodeFormStates.Replied);
        forms.Single(form => form.Id == name.Id).AnswerJson.ShouldBe("{\"q0\":\"Ada\"}");
        forms.Single(form => form.Id == pets.Id).AnswerJson.ShouldBe("{\"q0\":[\"Cats\",\"Hamsters\"]}");
        forms.Single(form => form.Id == note.Id).State.ShouldBe(OpenCodeFormStates.Cancelled);
        forms.Single(form => form.Id == note.Id).Message.ShouldBe("Not now.");
    }

    [Test]
    public async Task Interrupt_ends_a_turn_that_is_blocked_on_a_form_with_the_interrupted_event()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript { StallAfterSettlement = true });

        // The client declines the question at once, and the fixture's turn then stalls until it is interrupted.
        var prompt = fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"));
        await WaitUntilAsync(() => fixture.Server.GetForms().Any(static form => form.State == OpenCodeFormStates.Cancelled));

        (await fixture.Client.InterruptSessionAsync(fixture.SessionId)).ShouldBeTrue();
        var envelope = await prompt;

        envelope.Parts.Count.ShouldBe(1);
        envelope.Parts[0].GetProperty("type").GetString().ShouldBe("error");
        envelope.Parts[0].GetProperty("reason").GetString().ShouldBe("user");
        fixture.Server.InterruptRequestCount.ShouldBe(1);
        (await fixture.Client.InterruptSessionAsync(fixture.SessionId)).ShouldBeFalse();
    }

    [Test]
    public async Task Form_routes_report_an_authentication_failure_for_a_wrong_password()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript());
        using var http = new HttpClient { BaseAddress = fixture.Server.BaseUri };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, "wrong-password");

        var listed = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () => await client.ListFormsAsync(fixture.SessionId));
        var replied = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () =>
            await client.ReplyFormAsync(fixture.SessionId, "frm_missing", Answer(("q0", "x"))));
        var cancelled = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () =>
            await client.CancelFormAsync(fixture.SessionId, "frm_missing", "x"));
        var interrupted = await Should.ThrowAsync<OpenCodeAuthenticationException>(async () =>
            await client.InterruptSessionAsync(fixture.SessionId));

        new[] { listed, replied, cancelled, interrupted }.ShouldAllBe(static exception => exception.StatusCode == HttpStatusCode.Unauthorized);
        new[] { listed, replied, cancelled, interrupted }.ShouldAllBe(static exception => !exception.Message.Contains("wrong-password", StringComparison.Ordinal));
    }

    [Test]
    public async Task Question_is_handed_over_in_order_and_answered_with_fields_encoded_by_type()
    {
        await using var fixture = await QuestionFixture.StartAsync();
        var order = new List<string>();
        var interaction = new OpenCodeTurnInteraction(
            (request, _) =>
            {
                order.Add("handler");
                return Task.FromResult<OpenCodeUserInputResponse?>(new OpenCodeUserInputResponse(Answers(
                    ("Which color do you prefer?", ["Red"]),
                    ("Which pets do you like?", ["Cats"]))));
            },
            TimeSpan.FromMinutes(5),
            request => order.Add($"asked:{request.Questions.Count}"));

        var envelope = await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        order.ShouldBe(["asked:2", "handler"]);
        var form = fixture.Server.GetForms().Single();
        form.State.ShouldBe(OpenCodeFormStates.Replied);
        form.AnswerJson.ShouldBe("{\"q0\":\"Red\",\"q1\":[\"Cats\"]}");
        envelope.GetTextContent().ShouldContain("replied:{\"q0\":\"Red\",\"q1\":[\"Cats\"]}");
    }

    [Test]
    public async Task A_second_question_is_handed_over_while_the_first_is_still_pending()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript
        {
            PublishAllAtOnce = true,
            Forms = [SingleQuestionForm("First?"), SingleQuestionForm("Second?")],
        });
        var secondSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new ConcurrentBag<string>();
        var interaction = new OpenCodeTurnInteraction(
            async (request, token) =>
            {
                var question = request.Questions.Single().Question;
                handled.Add(question);
                if (question == "First?")
                {
                    // Only returns once the second question has reached the handler: a sequential loop would time out here.
                    await secondSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                }
                else
                {
                    secondSeen.TrySetResult();
                }

                return new OpenCodeUserInputResponse(Answers((question, ["yes"])));
            },
            TimeSpan.FromMinutes(5));

        await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        handled.OrderBy(static question => question, StringComparer.Ordinal).ShouldBe(["First?", "Second?"]);
        fixture.Server.GetForms().ShouldAllBe(static form => form.State == OpenCodeFormStates.Replied);
    }

    [Test]
    public async Task Handler_that_throws_declines_the_question_and_the_turn_completes()
    {
        await using var fixture = await QuestionFixture.StartAsync();
        var interaction = new OpenCodeTurnInteraction(
            static async (_, _) =>
            {
                await Task.Yield();
                throw new InvalidOperationException("handler failure");
            },
            TimeSpan.FromMinutes(5));

        var envelope = await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        envelope.GetTextContent().ShouldContain("cancelled:" + OpenCodeUserInput.DeclinedMessage);
        var form = fixture.Server.GetForms().Single();
        form.State.ShouldBe(OpenCodeFormStates.Cancelled);
        form.Message.ShouldBe(OpenCodeUserInput.DeclinedMessage);
    }

    [Test]
    public async Task Null_answer_declines_the_question()
    {
        await using var fixture = await QuestionFixture.StartAsync();
        var interaction = new OpenCodeTurnInteraction(
            static (_, _) => Task.FromResult<OpenCodeUserInputResponse?>(null),
            TimeSpan.FromMinutes(5));

        await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        var form = fixture.Server.GetForms().Single();
        form.State.ShouldBe(OpenCodeFormStates.Cancelled);
        form.Message.ShouldBe(OpenCodeUserInput.DeclinedMessage);
    }

    [Test]
    public async Task Question_timeout_cancels_the_form_with_the_timeout_message_even_when_the_handler_ignores_its_token()
    {
        await using var fixture = await QuestionFixture.StartAsync();
        var interaction = new OpenCodeTurnInteraction(
            static (_, _) => Task.Delay(Timeout.Infinite).ContinueWith(static _ => (OpenCodeUserInputResponse?)null, TaskScheduler.Default),
            TimeSpan.FromMilliseconds(300));

        var envelope = await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        envelope.GetTextContent().ShouldContain("cancelled:" + OpenCodeUserInput.TimeoutMessage);
        var form = fixture.Server.GetForms().Single();
        form.State.ShouldBe(OpenCodeFormStates.Cancelled);
        form.Message.ShouldBe(OpenCodeUserInput.TimeoutMessage);
    }

    [Test]
    public async Task Form_of_another_session_is_neither_handed_to_the_handler_nor_settled()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript
        {
            Forms =
            [
                new OpenCodeFixtureForm { SessionId = "ses_other_session", Fields = [new OpenCodeFixtureField("Other", "Other session?", false, ["x"])] },
                SingleQuestionForm("Own?"),
            ],
        });
        var asked = new ConcurrentBag<string>();
        var interaction = new OpenCodeTurnInteraction(
            (request, _) =>
            {
                asked.Add(request.Questions.Single().Question);
                return Task.FromResult<OpenCodeUserInputResponse?>(new OpenCodeUserInputResponse(Answers(("Own?", ["yes"]))));
            },
            TimeSpan.FromMinutes(5));

        await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        asked.ToArray().ShouldBe(["Own?"]);
        var forms = fixture.Server.GetForms();
        forms.Single(static form => form.SessionId == "ses_other_session").State.ShouldBe(OpenCodeFormStates.Pending);
        forms.Single(static form => form.SessionId != "ses_other_session").State.ShouldBe(OpenCodeFormStates.Replied);
    }

    [Test]
    public async Task Mcp_form_is_left_untouched_while_a_question_is_answered()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript
        {
            Forms = [McpForm("Elicitation", OpenCodeFormKinds.Mcp, type: false), SingleQuestionForm("Own?")],
        });
        var asked = new ConcurrentBag<string>();
        var interaction = new OpenCodeTurnInteraction(
            (request, _) =>
            {
                asked.Add(request.Questions.Single().Question);
                return Task.FromResult<OpenCodeUserInputResponse?>(new OpenCodeUserInputResponse(Answers(("Own?", ["yes"]))));
            },
            TimeSpan.FromMinutes(5));

        await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        asked.ToArray().ShouldBe(["Own?"]);
        fixture.Server.GetForms().Single(static form => form.Kind == OpenCodeFormKinds.Mcp).State.ShouldBe(OpenCodeFormStates.Pending);
    }

    [Test]
    public async Task Turn_that_ends_while_a_question_is_pending_cancels_the_form()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript { CompleteWithoutWaiting = true });
        var interaction = new OpenCodeTurnInteraction(
            static async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
            TimeSpan.FromMinutes(5));

        var envelope = await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        envelope.Parts.ShouldNotBeEmpty();
        var form = fixture.Server.GetForms().Single();
        form.State.ShouldBe(OpenCodeFormStates.Cancelled);
        form.Message.ShouldBe(OpenCodeUserInput.TurnEndedMessage);
    }

    [Test]
    public async Task A_slow_answer_past_the_request_timeout_still_succeeds()
    {
        await using var fixture = await QuestionFixture.StartAsync(requestTimeout: TimeSpan.FromSeconds(1));
        var interaction = new OpenCodeTurnInteraction(
            static async (request, _) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(2500));
                return new OpenCodeUserInputResponse(AnswerEvery(request, "late"));
            },
            TimeSpan.FromMinutes(1));

        var envelope = await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction);

        envelope.GetTextContent().ShouldContain("replied");
        fixture.Server.GetForms().Single().State.ShouldBe(OpenCodeFormStates.Replied);
    }

    [Test]
    public async Task An_agent_that_stalls_after_the_answer_still_times_out()
    {
        await using var fixture = await QuestionFixture.StartAsync(
            new OpenCodeFixtureQuestionScript { StallAfterSettlement = true },
            requestTimeout: TimeSpan.FromSeconds(1));
        var interaction = new OpenCodeTurnInteraction(
            static (request, _) => Task.FromResult<OpenCodeUserInputResponse?>(
                new OpenCodeUserInputResponse(AnswerEvery(request, "now"))),
            TimeSpan.FromMinutes(1));

        var started = DateTime.UtcNow;
        await Should.ThrowAsync<TimeoutException>(async () =>
            await fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction));

        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(15));
        fixture.Server.GetForms().Single().State.ShouldBe(OpenCodeFormStates.Replied);
    }

    [Test]
    public async Task Cancelling_an_interactive_turn_cancels_the_pending_form_and_interrupts_the_session()
    {
        await using var fixture = await QuestionFixture.StartAsync(new OpenCodeFixtureQuestionScript { StallAfterSettlement = true });
        using var cancel = new CancellationTokenSource();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new OpenCodeTurnInteraction(
            async (_, token) =>
            {
                handlerStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            },
            TimeSpan.FromMinutes(5));

        var prompt = fixture.Client.PromptAsync(fixture.SessionId, OpenCodeSessionPromptRequest.FromText("ask"), interaction, cancel.Token);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await prompt);
        var form = fixture.Server.GetForms().Single();
        form.State.ShouldBe(OpenCodeFormStates.Cancelled);
        form.Message.ShouldBe(OpenCodeUserInput.TurnEndedMessage);
        fixture.Server.InterruptRequestCount.ShouldBe(1);
    }

    private static OpenCodeFixtureForm SingleQuestionForm(string question)
    {
        return new OpenCodeFixtureForm
        {
            Fields = [new OpenCodeFixtureField("Header", question, MultiSelect: false, Options: ["yes", "no"])],
        };
    }

    private static OpenCodeFixtureForm McpForm(string header, string kind, bool type)
    {
        return new OpenCodeFixtureForm
        {
            Kind = kind,
            Fields = [new OpenCodeFixtureField(header, header + "?", MultiSelect: type, Options: type ? ["Cats", "Hamsters"] : [])],
        };
    }

    private static Dictionary<string, object> Answer(params (string Key, object Value)[] values)
    {
        return values.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, IReadOnlyList<string>> Answers(params (string Question, string[] Values)[] values)
    {
        return values.ToDictionary(
            static pair => pair.Question,
            static pair => (IReadOnlyList<string>)pair.Values,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> AnswerEvery(OpenCodeUserInputRequest request, string answer)
    {
        return request.Questions.ToDictionary(
            static question => question.Question,
            _ => (IReadOnlyList<string>)[answer],
            StringComparer.Ordinal);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not reached in time.");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// A fixture server whose <c>question-model</c> runs the given script, with a client that has a session on it.
    /// </summary>
    private sealed class QuestionFixture : IAsyncDisposable
    {
        private readonly HttpClient _http;

        private QuestionFixture(OpenCodeFixtureServerHandle server, HttpClient http, OpenCodeHttpClient client, string sessionId)
        {
            Server = server;
            _http = http;
            Client = client;
            SessionId = sessionId;
        }

        public OpenCodeFixtureServerHandle Server { get; }

        public OpenCodeHttpClient Client { get; }

        public string SessionId { get; }

        public static async Task<QuestionFixture> StartAsync(OpenCodeFixtureQuestionScript? script = null, TimeSpan? requestTimeout = null)
        {
            var server = await OpenCodeFixtureServer.StartAsync(new OpenCodeFixtureServerOptions
            {
                QuestionModelIds = ["fixture/question-model"],
                QuestionScript = script ?? new OpenCodeFixtureQuestionScript(),
            });
            var http = new HttpClient { BaseAddress = server.BaseUri, Timeout = requestTimeout ?? TimeSpan.FromSeconds(30) };
            var client = new OpenCodeHttpClient(http, directory: null, workspace: null, server.ServerPassword);
            var session = await client.CreateSessionAsync("questions");
            await client.SetSessionModelAsync(session.Id, new OpenCodeModelSelection { ProviderId = "fixture", ModelId = "question-model" });
            return new QuestionFixture(server, http, client, session.Id);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            _http.Dispose();
            await Server.DisposeAsync();
        }
    }

    private static string ExpectedBasic()
    {
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{Password}"));
    }

    private static string Event(string type, object data)
    {
        return JsonSerializer.Serialize(new { id = $"evt_{Guid.NewGuid():N}", type, data });
    }

    private static string Events(params string[] events)
    {
        return string.Concat(events.Select(static json => $"data: {json}\n\n"));
    }

    private static ScriptedHandler PromptScript(string eventsBody)
    {
        return new ScriptedHandler(request => request.PathAndQuery switch
        {
            "/api/event" => Sse(eventsBody),
            _ when request.Method == "POST" && request.PathAndQuery == $"/api/session/{SessionId}/prompt" =>
                Json($"{{\"data\":{{\"id\":\"msg_1\",\"sessionID\":\"{SessionId}\",\"type\":\"user\",\"payload\":{{\"text\":\"hi\"}},\"delivery\":\"steer\"}}}}"),
            _ => Status(HttpStatusCode.NotFound, "{}"),
        });
    }

    [Test]
    public async Task Model_listing_reports_a_text_only_model_as_not_accepting_images()
    {
        var handler = new ScriptedHandler(static _ => Json("{\"data\":[{\"id\":\"ll-alpha\",\"modelID\":\"ll-alpha\",\"providerID\":\"opencode\",\"capabilities\":{\"tools\":true,\"input\":[\"text\"],\"output\":[\"text\"]}}]}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var models = await client.ListModelsAsync();

        handler.Requests.Single().PathAndQuery.ShouldBe("/api/model");
        var model = models.Single();
        model.ProviderId.ShouldBe("opencode");
        model.Id.ShouldBe("ll-alpha");
        model.InputCapabilities.ShouldBe(["text"]);
        model.AcceptsImage.ShouldBeFalse();
    }

    [Test]
    public async Task Model_listing_reports_an_image_capable_model_as_accepting_images()
    {
        var handler = new ScriptedHandler(static _ => Json("{\"data\":[{\"id\":\"nemotron\",\"modelID\":\"nemotron\",\"providerID\":\"opencode\",\"capabilities\":{\"input\":[\"text\",\"image\"]}}]}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var model = (await client.ListModelsAsync()).Single();

        model.InputCapabilities.ShouldBe(["text", "image"]);
        model.AcceptsImage.ShouldBeTrue();
    }

    [Test]
    public async Task Model_without_a_capabilities_member_has_no_input_capabilities()
    {
        var handler = new ScriptedHandler(static _ => Json("{\"data\":[{\"id\":\"bare\",\"modelID\":\"bare\",\"providerID\":\"opencode\"}]}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var model = (await client.ListModelsAsync()).Single();

        model.InputCapabilities.ShouldBeEmpty();
        model.AcceptsImage.ShouldBeFalse();
    }

    [Test]
    public async Task Failing_model_listing_throws_to_its_caller_and_later_calls_still_work()
    {
        var handler = new ScriptedHandler(static request => request.PathAndQuery == "/api/model"
            ? Status(HttpStatusCode.InternalServerError, "{\"_tag\":\"InternalError\",\"message\":\"model listing failed\"}")
            : Json("{\"version\":\"2.0.32\",\"pid\":1,\"urls\":[],\"paths\":{\"tmp\":\"/tmp\"}}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://stub/") };
        using var client = new OpenCodeHttpClient(http, directory: null, workspace: null, Password);

        var exception = await Should.ThrowAsync<OpenCodeApiException>(async () => await client.ListModelsAsync());
        var health = await client.HealthAsync();

        exception.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        health.Version.ShouldBe("2.0.32");
    }

    private static HttpResponseMessage Json(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static HttpResponseMessage Html(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
    }

    private static HttpResponseMessage Sse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    }

    private static HttpResponseMessage Status(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record RecordedRequest(string Method, string PathAndQuery, string? Authorization, string Body);

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<RecordedRequest, HttpResponseMessage> _responder;
        private readonly List<RecordedRequest> _requests = [];

        public ScriptedHandler(Func<RecordedRequest, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public IReadOnlyList<RecordedRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(),
                body);
            lock (_requests)
            {
                _requests.Add(recorded);
            }

            return _responder(recorded);
        }
    }

    /// <summary>
    /// Returns the first chunk, then blocks until the read is cancelled, like an event stream that never ends.
    /// </summary>
    private sealed class HangingEventStream : Stream
    {
        private readonly byte[] _first;
        private bool _sentFirst;

        public HangingEventStream(byte[] first)
        {
            _first = first;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                _first.CopyTo(buffer);
                return _first.Length;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
