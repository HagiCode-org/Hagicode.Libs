using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Providers.Codex;
using HagiCode.Libs.Providers.Codex.AppServer;
using ManagedCode.CodexSharpSDK.Client;
using ManagedCode.CodexSharpSDK.Models;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Drives <see cref="CodexAppServerClient"/> against a scripted app-server over text streams. The request and reply shapes are
/// checked against the pinned codex-cli 0.163.0 subset and turn-steer fixture in Fixtures/Codex/AppServer.
/// </summary>
public sealed class CodexAppServerClientTests
{
    private const string FeatureName = "default_mode_request_user_input";
    private const string ThreadId = "thread-1";
    private const string TurnId = "turn-1";

    [Test]
    public async Task Blocking_question_reaches_the_handler_and_is_answered_with_the_original_request_id()
    {
        using var cts = Deadline();
        CodexUserQuestionRequest? seen = null;
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(
            server.ClientReader,
            server.ClientWriter,
            (request, _) =>
            {
                seen = request;
                return Task.FromResult<CodexUserQuestionResponse?>(new CodexUserQuestionResponse(
                    new Dictionary<string, IReadOnlyList<string>> { ["color"] = ["red"] }));
            },
            Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Request(JsonValue.Create(7)!, "item/tool/requestUserInput", QuestionParams(isBlocking: true));

        var reply = await server.ExpectReplyAsync(JsonValue.Create(7)!, cts.Token);
        reply["result"]!["answers"]!["color"]!["answers"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["red"]);
        reply["error"].ShouldBeNull();

        server.Notify("item/completed", AgentMessage("msg-1", "red"));
        server.Notify("turn/completed", TurnCompleted("completed"));
        var collected = await events;

        seen.ShouldNotBeNull();
        seen.RequestId.ShouldBe("7");
        seen.IsBlocking.ShouldBeTrue();
        seen.ThreadId.ShouldBe(ThreadId);
        seen.TurnId.ShouldBe(TurnId);
        seen.Questions.Single().Id.ShouldBe("color");
        seen.Questions.Single().Options.Select(option => option.Label).ShouldBe(["red", "blue"]);
        collected.OfType<ItemCompletedEvent>().Single().Item.ShouldBeOfType<AgentMessageItem>();
        collected.Last().ShouldBeOfType<TurnCompletedEvent>().Result.ShouldBe("red");
    }

    [Test]
    public async Task Expired_blocking_question_gets_an_error_reply_and_the_turn_still_completes()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(
            server.ClientReader,
            server.ClientWriter,
            (_, token) => WaitForever(token),
            TimeSpan.FromMilliseconds(200));
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Request(JsonValue.Create(3)!, "item/tool/requestUserInput", QuestionParams(isBlocking: true));

        var reply = await server.ExpectReplyAsync(JsonValue.Create(3)!, cts.Token);
        reply["error"]!["code"]!.GetValue<int>().ShouldBe(-32000);
        reply["result"].ShouldBeNull();

        server.Notify("turn/completed", TurnCompleted("completed"));
        var collected = await events;
        collected.Last().ShouldBeOfType<TurnCompletedEvent>();
    }

    [Test]
    public async Task Non_blocking_question_is_answered_like_a_blocking_one_so_the_turn_is_never_left_waiting()
    {
        // Gate G5: the observed server holds the turn on a request reported as non-blocking, so every request is replied to.
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(
            server.ClientReader,
            server.ClientWriter,
            (_, token) => WaitForever(token),
            TimeSpan.FromMilliseconds(200));
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Request(JsonValue.Create(4)!, "item/tool/requestUserInput", QuestionParams(isBlocking: false));

        var reply = await server.ExpectReplyAsync(JsonValue.Create(4)!, cts.Token);
        reply["error"].ShouldNotBeNull();

        server.Notify("turn/completed", TurnCompleted("completed"));
        (await events).Last().ShouldBeOfType<TurnCompletedEvent>();
    }

    [Test]
    public async Task Missing_isBlocking_is_treated_as_blocking()
    {
        using var cts = Deadline();
        CodexUserQuestionRequest? seen = null;
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(
            server.ClientReader,
            server.ClientWriter,
            (request, _) =>
            {
                seen = request;
                return Task.FromResult<CodexUserQuestionResponse?>(null);
            },
            Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        var parameters = QuestionParams(isBlocking: true);
        parameters.Remove("isBlocking");
        server.Request(JsonValue.Create(5)!, "item/tool/requestUserInput", parameters);

        await server.ExpectReplyAsync(JsonValue.Create(5)!, cts.Token);
        server.Notify("turn/completed", TurnCompleted("completed"));
        await events;

        seen.ShouldNotBeNull();
        seen.IsBlocking.ShouldBeTrue();
    }

    [Test]
    public async Task autoResolutionMs_is_read_without_starting_a_timer()
    {
        using var cts = Deadline();
        var release = new TaskCompletionSource<CodexUserQuestionResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CodexUserQuestionRequest? seen = null;
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(
            server.ClientReader,
            server.ClientWriter,
            (request, token) =>
            {
                seen = request;
                return release.Task.WaitAsync(token);
            },
            Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        var parameters = QuestionParams(isBlocking: true);
        parameters["autoResolutionMs"] = 150;
        server.Request(JsonValue.Create(6)!, "item/tool/requestUserInput", parameters);

        await Task.Delay(800, cts.Token);
        server.HasReply(JsonValue.Create(6)!).ShouldBeFalse();
        seen.ShouldNotBeNull();
        seen.AutoResolutionMs.ShouldBe(150L);

        release.SetResult(new CodexUserQuestionResponse(new Dictionary<string, IReadOnlyList<string>> { ["color"] = ["blue"] }));
        var reply = await server.ExpectReplyAsync(JsonValue.Create(6)!, cts.Token);
        reply["result"]!["answers"]!["color"]!["answers"]!.AsArray()[0]!.GetValue<string>().ShouldBe("blue");

        server.Notify("turn/completed", TurnCompleted("completed"));
        await events;
    }

    [Test]
    public async Task Repeated_answer_for_a_request_is_rejected()
    {
        using var cts = Deadline();
        var open = OpenQuestion();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, open.Handler, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Request(JsonValue.Create(8)!, "item/tool/requestUserInput", QuestionParams(isBlocking: true));
        await open.Opened.Task.WaitAsync(cts.Token);
        var answer = new CodexUserQuestionResponse(new Dictionary<string, IReadOnlyList<string>> { ["color"] = ["red"] });

        (await client.RespondToUserQuestionAsync("8", answer, cts.Token)).ShouldBeTrue();
        (await client.RespondToUserQuestionAsync("8", null, cts.Token)).ShouldBeFalse();

        var reply = await server.ExpectReplyAsync(JsonValue.Create(8)!, cts.Token);
        reply["result"].ShouldNotBeNull();
        reply["error"].ShouldBeNull();

        server.Notify("turn/completed", TurnCompleted("completed"));
        await events;
    }

    [Test]
    public async Task Stop_declines_open_questions_and_interrupts_the_turn()
    {
        using var cts = Deadline();
        var open = OpenQuestion();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, open.Handler, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        using var stopCts = new CancellationTokenSource();
        var events = StartTurn(client, server, stopCts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Request(JsonValue.Create(9)!, "item/tool/requestUserInput", QuestionParams(isBlocking: true));
        await open.Opened.Task.WaitAsync(cts.Token);

        await stopCts.CancelAsync();

        var declined = await server.ExpectReplyAsync(JsonValue.Create(9)!, cts.Token);
        declined["error"].ShouldNotBeNull();
        var interrupt = await server.ExpectRequestAsync("turn/interrupt", cts.Token);
        interrupt["params"]!["threadId"]!.GetValue<string>().ShouldBe(ThreadId);
        interrupt["params"]!["turnId"]!.GetValue<string>().ShouldBe(TurnId);
        server.Send(new JsonObject { ["id"] = interrupt["id"]!.DeepClone(), ["result"] = new JsonObject() });

        await Should.ThrowAsync<OperationCanceledException>(async () => await events);
    }

    [Test]
    public async Task Feature_check_reads_every_page_of_the_feature_list()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();

        var check = client.HasExperimentalFeatureAsync(FeatureName, cts.Token);
        await server.AnswerAsync("experimentalFeature/list", new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["name"] = "other" }),
            ["nextCursor"] = "page-2"
        }, cts.Token);
        var second = await server.ExpectRequestAsync("experimentalFeature/list", cts.Token);
        second["params"]!["cursor"]!.GetValue<string>().ShouldBe("page-2");
        server.Send(new JsonObject
        {
            ["id"] = second["id"]!.DeepClone(),
            ["result"] = new JsonObject
            {
                ["data"] = new JsonArray(new JsonObject { ["name"] = FeatureName }),
                ["nextCursor"] = null
            }
        });

        (await check).ShouldBeTrue();
    }

    [Test]
    public async Task Feature_check_is_false_when_the_feature_is_not_listed()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();

        var check = client.HasExperimentalFeatureAsync(FeatureName, cts.Token);
        await server.AnswerAsync("experimentalFeature/list", new JsonObject
        {
            ["data"] = new JsonArray(),
            ["nextCursor"] = null
        }, cts.Token);

        (await check).ShouldBeFalse();
    }

    [Test]
    public async Task Unsupported_server_request_gets_a_method_not_found_error()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        server.Request(JsonValue.Create(11)!, "account/chatgptAuthTokens/refresh", new JsonObject());

        var reply = await server.ExpectReplyAsync(JsonValue.Create(11)!, cts.Token);
        reply["error"]!["code"]!.GetValue<int>().ShouldBe(-32601);
    }

    [Test]
    public async Task Approval_request_is_declined_with_the_pinned_decision_shape()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        server.Request(JsonValue.Create(12)!, "item/commandExecution/requestApproval", new JsonObject());

        var reply = await server.ExpectReplyAsync(JsonValue.Create(12)!, cts.Token);
        reply["result"]!["decision"]!.GetValue<string>().ShouldBe("decline");
    }

    [Test]
    public async Task Turn_events_map_items_usage_and_terminal_status()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Notify("turn/started", new JsonObject { ["threadId"] = ThreadId, ["turn"] = new JsonObject { ["id"] = TurnId } });
        server.Notify("item/started", new JsonObject
        {
            ["threadId"] = ThreadId,
            ["turnId"] = TurnId,
            ["item"] = new JsonObject
            {
                ["type"] = "commandExecution",
                ["id"] = "cmd-1",
                ["command"] = "ls",
                ["aggregatedOutput"] = null,
                ["exitCode"] = null,
                ["status"] = "inProgress"
            }
        });
        server.Notify("thread/tokenUsage/updated", TokenUsage(10, 2, 5));
        server.Notify("thread/tokenUsage/updated", TokenUsage(4, 1, 3));
        server.Notify("item/completed", new JsonObject
        {
            ["threadId"] = ThreadId,
            ["turnId"] = TurnId,
            ["item"] = new JsonObject
            {
                ["type"] = "commandExecution",
                ["id"] = "cmd-1",
                ["command"] = "ls",
                ["aggregatedOutput"] = "a\n",
                ["exitCode"] = 0,
                ["status"] = "completed"
            }
        });
        server.Notify("turn/completed", TurnCompleted("completed"));

        var collected = await events;
        collected.Select(e => e.GetType()).ShouldBe([typeof(TurnStartedEvent), typeof(ItemStartedEvent), typeof(ItemCompletedEvent), typeof(TurnCompletedEvent)]);
        var command = collected.OfType<ItemCompletedEvent>().Single().Item.ShouldBeOfType<CommandExecutionItem>();
        command.ExitCode.ShouldBe(0);
        command.Status.ShouldBe(CommandExecutionStatus.Completed);
        var usage = collected.OfType<TurnCompletedEvent>().Single().Usage;
        usage.InputTokens.ShouldBe(14);
        usage.CachedInputTokens.ShouldBe(3);
        usage.OutputTokens.ShouldBe(8);
    }

    [Test]
    public async Task Interrupted_turn_status_maps_to_a_failed_turn()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Notify("turn/completed", TurnCompleted("interrupted"));

        var last = (await events).Last();
        last.ShouldBeOfType<TurnFailedEvent>().Error.Message.ShouldContain("interrupted");
    }

    [Test]
    public async Task A_closed_connection_fails_the_turn_instead_of_hanging()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var events = StartTurn(client, server, cts.Token);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        server.Close();

        await Should.ThrowAsync<IOException>(async () => await events);
    }

    [Test]
    public void Version_floor_accepts_0_163_0_and_newer_and_refuses_older_releases()
    {
        CodexAppServerSession.MeetsMinimumCliVersion("0.163.0").ShouldBeTrue();
        CodexAppServerSession.MeetsMinimumCliVersion("0.163").ShouldBeTrue();
        CodexAppServerSession.MeetsMinimumCliVersion("0.164.0-beta.1").ShouldBeTrue();
        CodexAppServerSession.MeetsMinimumCliVersion("codex-cli 0.163.0").ShouldBeTrue();
        CodexAppServerSession.MeetsMinimumCliVersion("0.162.9").ShouldBeFalse();
        CodexAppServerSession.MeetsMinimumCliVersion("0.135.0").ShouldBeFalse();
        CodexAppServerSession.MeetsMinimumCliVersion("").ShouldBeFalse();
        CodexAppServerSession.MeetsMinimumCliVersion(null).ShouldBeFalse();
        CodexAppServerSession.MeetsMinimumCliVersion("unknown").ShouldBeFalse();
    }

    [Test]
    public void Thread_parameters_set_the_feature_flag_and_keep_the_existing_config()
    {
        var config = JsonNode.Parse("""{"model_reasoning_effort":"high","features":{"hooks":true}}""")!.AsObject();
        var options = new ThreadOptions
        {
            Model = "gpt-5.5",
            WorkingDirectory = "/repo",
            SandboxMode = SandboxMode.ReadOnly,
            ApprovalPolicy = ApprovalMode.Never,
            DisabledFeatures = ["plugins"]
        };

        var parameters = CodexAppServerSession.BuildThreadParameters(options, config);

        parameters["model"]!.GetValue<string>().ShouldBe("gpt-5.5");
        parameters["cwd"]!.GetValue<string>().ShouldBe("/repo");
        parameters["sandbox"]!.GetValue<string>().ShouldBe("read-only");
        parameters["approvalPolicy"]!.GetValue<string>().ShouldBe("never");
        var sent = parameters["config"]!.AsObject();
        sent["model_reasoning_effort"]!.GetValue<string>().ShouldBe("high");
        sent["features"]!["hooks"]!.GetValue<bool>().ShouldBeTrue();
        sent["features"]!["default_mode_request_user_input"]!.GetValue<bool>().ShouldBeTrue();
        sent["features"]!["plugins"]!.GetValue<bool>().ShouldBeFalse();
        config.ContainsKey("features").ShouldBeTrue();
        config["features"]!.AsObject().ContainsKey("default_mode_request_user_input").ShouldBeFalse();
    }

    [Test]
    public void Turn_parameters_carry_the_reasoning_effort_and_writable_roots_for_workspace_write()
    {
        var parameters = CodexAppServerSession.BuildTurnParameters(new ThreadOptions
        {
            ModelReasoningEffort = ModelReasoningEffort.Medium,
            SandboxMode = SandboxMode.WorkspaceWrite,
            AdditionalDirectories = ["/shared/a", "/shared/b"],
            NetworkAccessEnabled = true
        });

        parameters["effort"]!.GetValue<string>().ShouldBe("medium");
        var policy = parameters["sandboxPolicy"]!.AsObject();
        policy["type"]!.GetValue<string>().ShouldBe("workspaceWrite");
        policy["writableRoots"]!.AsArray().Select(node => node!.GetValue<string>()).ShouldBe(["/shared/a", "/shared/b"]);
        policy["networkAccess"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Test]
    public void Scripted_question_has_every_field_the_pinned_schema_requires()
    {
        var pinned = LoadPinnedSubset();
        var required = pinned["item/tool/requestUserInput"]!["params"]!["required"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();

        var parameters = QuestionParams(isBlocking: true);

        foreach (var key in required)
        {
            parameters.ContainsKey(key).ShouldBeTrue($"the scripted request must carry '{key}' as the pinned schema requires");
        }
    }

    [Test]
    public void Turn_steer_fixture_correlates_acceptance_and_refuses_a_stale_turn_without_starting_another_turn()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codex", "AppServer", "turn-steer-0.163.0.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(fixturePath));
        var root = fixture.RootElement;
        root.GetProperty("codexCliVersion").GetString().ShouldBe("0.163.0");

        var activeTurn = root.GetProperty("activeTurn");
        var threadId = activeTurn.GetProperty("threadId").GetString();
        var activeTurnId = activeTurn.GetProperty("turnId").GetString();
        var transactions = root.GetProperty("transactions").EnumerateArray().ToArray();
        transactions.Length.ShouldBe(2);

        foreach (var transaction in transactions)
        {
            var request = transaction.GetProperty("request");
            request.GetProperty("method").GetString().ShouldBe("turn/steer");
            request.GetProperty("params").GetProperty("threadId").GetString().ShouldBe(threadId);
            request.GetProperty("params").GetProperty("input").GetArrayLength().ShouldBe(1);
            transaction.GetProperty("response").GetProperty("id").GetInt32()
                .ShouldBe(request.GetProperty("id").GetInt32());
        }

        var accepted = transactions[0];
        accepted.GetProperty("name").GetString().ShouldBe("accepted");
        accepted.GetProperty("request").GetProperty("params").GetProperty("expectedTurnId").GetString()
            .ShouldBe(activeTurnId);
        accepted.GetProperty("response").GetProperty("result").GetProperty("turnId").GetString()
            .ShouldBe(activeTurnId);

        var stale = transactions[1];
        stale.GetProperty("name").GetString().ShouldBe("stale-turn-refused");
        stale.GetProperty("request").GetProperty("params").GetProperty("expectedTurnId").GetString()
            .ShouldNotBe(activeTurnId);
        stale.GetProperty("response").GetProperty("error").GetProperty("code").GetInt32()
            .ShouldBe(-32600);
        stale.GetProperty("response").GetProperty("error").GetProperty("message").GetString()
            .ShouldContain(activeTurnId!);

        var pinned = LoadPinnedSubset();
        var required = pinned["turn/steer"]!["params"]!["required"]!.AsArray()
            .Select(node => node!.GetValue<string>())
            .ToArray();
        foreach (var key in required)
        {
            transactions[0].GetProperty("request").GetProperty("params").TryGetProperty(key, out _)
                .ShouldBeTrue($"the captured request must carry '{key}' as the pinned schema requires");
        }
    }

    [Test]
    public async Task Equal_text_steers_and_duplicate_acknowledgements_keep_the_prompt_and_outcomes_correlated()
    {
        using var cts = Deadline();
        const string sharedText = "Ask me.";
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var steering = new CodexRunSteering();
        var outcomes = new List<CodexSteerOutcomeEventArgs>();
        var outcomesResolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) =>
        {
            outcomes.Add(args);
            if (outcomes.Count == 2)
            {
                outcomesResolved.TrySetResult();
            }
        };
        steering.Attach((steerId, text, token) =>
            client.TrySteerAsync(steerId, ThreadId, text, TimeSpan.FromSeconds(5), steering, token));

        var eventsTask = StartTurn(client, server, cts.Token, sharedText);
        var startRequest = await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        startRequest["params"]!["input"]![0]!["text"]!.GetValue<string>().ShouldBe(sharedText);
        server.Notify("turn/started", new JsonObject
        {
            ["threadId"] = ThreadId,
            ["turn"] = new JsonObject { ["id"] = TurnId, ["status"] = "inProgress", ["items"] = new JsonArray() }
        });

        var firstTask = steering.SteerAsync("steer-one", sharedText, cts.Token);
        var firstRequest = await server.ExpectRequestAsync("turn/steer", cts.Token);
        firstRequest["params"]!["clientUserMessageId"]!.GetValue<string>().ShouldBe("steer-one");
        firstRequest["params"]!["expectedTurnId"]!.GetValue<string>().ShouldBe(TurnId);
        firstRequest["params"]!["input"]![0]!["text"]!.GetValue<string>().ShouldBe(sharedText);
        server.Send(new JsonObject
        {
            ["id"] = firstRequest["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        (await firstTask).ShouldBe(CodexSteerResult.Sent);

        var secondTask = steering.SteerAsync("steer-two", sharedText, cts.Token);
        var secondRequest = await server.ExpectRequestAsync("turn/steer", cts.Token);
        secondRequest["params"]!["clientUserMessageId"]!.GetValue<string>().ShouldBe("steer-two");
        secondRequest["id"]!.ToJsonString().ShouldNotBe(firstRequest["id"]!.ToJsonString());
        server.Send(new JsonObject
        {
            ["id"] = secondRequest["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        (await secondTask).ShouldBe(CodexSteerResult.Sent);

        // A duplicate response has no pending JSON-RPC request and must not report the first steer a second time.
        server.Send(new JsonObject
        {
            ["id"] = firstRequest["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        server.Notify("item/completed", new JsonObject
        {
            ["threadId"] = ThreadId,
            ["turnId"] = TurnId,
            ["item"] = new JsonObject { ["type"] = "userMessage", ["id"] = "steer-echo", ["text"] = sharedText }
        });
        server.Notify("turn/completed", TurnCompleted("completed"));

        var events = await eventsTask;
        await outcomesResolved.Task.WaitAsync(cts.Token);
        outcomes.Select(outcome => outcome.SteerId).ShouldBe(["steer-one", "steer-two"]);
        outcomes.Select(outcome => outcome.Outcome).ShouldBe([CodexSteerOutcome.Consumed, CodexSteerOutcome.Consumed]);
        events.OfType<ItemCompletedEvent>().ShouldBeEmpty();
        events.OfType<TurnCompletedEvent>().Count().ShouldBe(1);
        server.Received.Count(message => message["method"]?.GetValue<string>() == "turn/start").ShouldBe(1);
    }

    [Test]
    public async Task Concurrent_question_responses_and_steering_keep_one_image_turn_without_a_prompt_retry()
    {
        using var cts = Deadline();
        const string prompt = "Ask me.";
        var firstAnswer = new TaskCompletionSource<CodexUserQuestionResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAnswer = new TaskCompletionSource<CodexUserQuestionResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var questionHandlersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCount = 0;
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(
            server.ClientReader,
            server.ClientWriter,
            (request, token) =>
            {
                if (Interlocked.Increment(ref handlerCount) == 2)
                {
                    questionHandlersEntered.TrySetResult();
                }

                return (request.RequestId switch
                {
                    "41" => firstAnswer,
                    "42" => secondAnswer,
                    _ => throw new InvalidOperationException($"Unexpected question id {request.RequestId}.")
                }).Task.WaitAsync(token);
            },
            Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var steering = new CodexRunSteering();
        var steerOutcome = new TaskCompletionSource<CodexSteerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) => steerOutcome.TrySetResult(args.Outcome);
        AttachSteering(client, steering, TimeSpan.FromSeconds(5));

        var imageBytes = Convert.FromHexString("89504e470d0a1a0a");
        var input = CodexAppServerSession.BuildTurnInput(prompt, [new CodexImageAttachment("image/png", imageBytes)]);
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsTask = CollectAsync(
            client.RunTurnAsync(ThreadId, input, new JsonObject { ["effort"] = "medium" }, cts.Token),
            turnStarted);
        var turnStart = await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        var turnInput = turnStart["params"]!["input"]!.AsArray();
        turnInput.Count.ShouldBe(2);
        turnInput[0]!["text"]!.GetValue<string>().ShouldBe(prompt);
        turnInput[1]!["type"]!.GetValue<string>().ShouldBe("image");
        turnInput[1]!["url"]!.GetValue<string>().ShouldBe("data:image/png;base64," + Convert.ToBase64String(imageBytes));
        NotifyTurnStarted(server);
        await turnStarted.Task.WaitAsync(cts.Token);

        server.Request(JsonValue.Create(41)!, "item/tool/requestUserInput", QuestionParams(isBlocking: true));
        server.Request(JsonValue.Create(42)!, "item/tool/requestUserInput", QuestionParams(isBlocking: true));
        await questionHandlersEntered.Task.WaitAsync(cts.Token);

        var send = steering.SteerAsync("steer-with-questions-open", prompt, cts.Token);
        var steerRequest = await server.ExpectRequestAsync("turn/steer", cts.Token);
        steerRequest["params"]!["threadId"]!.GetValue<string>().ShouldBe(ThreadId);
        steerRequest["params"]!["expectedTurnId"]!.GetValue<string>().ShouldBe(TurnId);
        steerRequest["params"]!["input"]![0]!["text"]!.GetValue<string>().ShouldBe(prompt);
        server.Send(new JsonObject
        {
            ["id"] = steerRequest["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        (await send).ShouldBe(CodexSteerResult.Sent);
        (await steerOutcome.Task.WaitAsync(cts.Token)).ShouldBe(CodexSteerOutcome.Consumed);

        firstAnswer.SetResult(new CodexUserQuestionResponse(
            new Dictionary<string, IReadOnlyList<string>> { ["color"] = ["red"] }));
        secondAnswer.SetResult(new CodexUserQuestionResponse(
            new Dictionary<string, IReadOnlyList<string>> { ["color"] = ["blue"] }));
        var firstReply = await server.ExpectReplyAsync(JsonValue.Create(41)!, cts.Token);
        var secondReply = await server.ExpectReplyAsync(JsonValue.Create(42)!, cts.Token);
        firstReply["result"]!["answers"]!["color"]!["answers"]![0]!.GetValue<string>().ShouldBe("red");
        secondReply["result"]!["answers"]!["color"]!["answers"]![0]!.GetValue<string>().ShouldBe("blue");

        server.Notify("item/completed", new JsonObject
        {
            ["threadId"] = ThreadId,
            ["turnId"] = TurnId,
            ["item"] = new JsonObject { ["type"] = "userMessage", ["id"] = "steer-with-questions-open", ["text"] = prompt }
        });
        server.Notify("turn/completed", TurnCompleted("completed"));
        var events = await eventsTask;

        events.OfType<ItemCompletedEvent>().ShouldBeEmpty();
        events.OfType<TurnCompletedEvent>().Count().ShouldBe(1);
        server.Received.Count(message => message["method"]?.GetValue<string>() == "turn/start").ShouldBe(1);
        server.Received.Count(message => message["method"]?.GetValue<string>() == "turn/steer").ShouldBe(1);
    }

    [Test]
    public async Task A_terminal_turn_failure_resolves_pending_steering_without_a_hidden_continuation()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var steering = new CodexRunSteering();
        var outcome = new TaskCompletionSource<CodexSteerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) => outcome.TrySetResult(args.Outcome);
        AttachSteering(client, steering, TimeSpan.FromSeconds(5));

        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsTask = StartTurn(client, server, cts.Token, turnStarted: turnStarted);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        NotifyTurnStarted(server);
        await turnStarted.Task.WaitAsync(cts.Token);

        var send = steering.SteerAsync("steer-before-failure", "continue the current task", cts.Token);
        await server.ExpectRequestAsync("turn/steer", cts.Token);
        (await send).ShouldBe(CodexSteerResult.Sent);
        server.Notify("turn/completed", TurnCompleted("interrupted"));

        var events = await eventsTask;
        events.Last().ShouldBeOfType<TurnFailedEvent>();
        (await outcome.Task.WaitAsync(cts.Token)).ShouldBe(CodexSteerOutcome.NotConsumed);
        server.Received.Count(message => message["method"]?.GetValue<string>() == "turn/start").ShouldBe(1);
        server.Received.Count(message => message["method"]?.GetValue<string>() == "turn/steer").ShouldBe(1);
    }

    [Test]
    public async Task A_turn_completion_keeps_a_pending_steer_correlated_until_its_deadline_or_acknowledgement()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var steering = new CodexRunSteering();
        var outcome = new TaskCompletionSource<CodexSteerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) => outcome.TrySetResult(args.Outcome);
        AttachSteering(client, steering, TimeSpan.FromSeconds(5));
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsTask = StartTurn(client, server, cts.Token, turnStarted: turnStarted);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        NotifyTurnStarted(server);
        await turnStarted.Task.WaitAsync(cts.Token);

        var send = steering.SteerAsync("steer-after-turn-end", "wait for the late acknowledgement", cts.Token);
        var request = await server.ExpectRequestAsync("turn/steer", cts.Token);
        (await send).ShouldBe(CodexSteerResult.Sent);
        server.Notify("turn/completed", TurnCompleted("completed"));
        (await eventsTask.WaitAsync(cts.Token)).Last().ShouldBeOfType<TurnCompletedEvent>();
        outcome.Task.IsCompleted.ShouldBeFalse();
        (await steering.SteerAsync("too-late", "the turn is already complete", cts.Token))
            .ShouldBe(CodexSteerResult.NotSteerable);

        server.Send(new JsonObject
        {
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        (await outcome.Task.WaitAsync(cts.Token)).ShouldBe(CodexSteerOutcome.Consumed);
    }

    [Test]
    public async Task Stale_acknowledgement_deadline_expiry_and_late_replies_resolve_each_steer_once()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var steering = new CodexRunSteering();
        var outcomes = new List<(string Id, CodexSteerOutcome Outcome)>();
        var outcomesResolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOutcomeResolved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) =>
        {
            outcomes.Add((args.SteerId, args.Outcome));
            if (outcomes.Count == 1)
            {
                firstOutcomeResolved.TrySetResult();
            }

            if (outcomes.Count == 2)
            {
                outcomesResolved.TrySetResult();
            }
        };
        AttachSteering(client, steering, TimeSpan.FromMilliseconds(150));
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsTask = StartTurn(client, server, cts.Token, turnStarted: turnStarted);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        NotifyTurnStarted(server);
        await turnStarted.Task.WaitAsync(cts.Token);

        var staleSend = steering.SteerAsync("stale", "do not retarget", cts.Token);
        var staleRequest = await server.ExpectRequestAsync("turn/steer", cts.Token);
        (await staleSend).ShouldBe(CodexSteerResult.Sent);
        server.Send(new JsonObject
        {
            ["id"] = staleRequest["id"]!.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "expected active turn id but found another turn" }
        });
        await firstOutcomeResolved.Task.WaitAsync(cts.Token);

        var deadlineSend = steering.SteerAsync("deadline", "wait only for the configured budget", cts.Token);
        var deadlineRequest = await server.ExpectRequestAsync("turn/steer", cts.Token);
        (await deadlineSend).ShouldBe(CodexSteerResult.Sent);
        await outcomesResolved.Task.WaitAsync(cts.Token);
        outcomes.ShouldBe([
            ("stale", CodexSteerOutcome.NotConsumed),
            ("deadline", CodexSteerOutcome.NotConsumed)
        ]);

        server.Send(new JsonObject
        {
            ["id"] = staleRequest["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        server.Send(new JsonObject
        {
            ["id"] = deadlineRequest["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        server.Notify("turn/completed", TurnCompleted("completed"));
        await eventsTask.WaitAsync(cts.Token);
        outcomes.Count.ShouldBe(2);
    }

    [Test]
    public async Task Cancelling_a_run_resolves_accepted_steers_before_interrupt_cleanup()
    {
        using var deadline = Deadline();
        using var runCancellation = new CancellationTokenSource();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, deadline.Token);

        var steering = new CodexRunSteering();
        var outcome = new TaskCompletionSource<CodexSteerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) => outcome.TrySetResult(args.Outcome);
        AttachSteering(client, steering, TimeSpan.FromSeconds(5));
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsTask = StartTurn(client, server, runCancellation.Token, turnStarted: turnStarted);
        await server.AnswerAsync("turn/start", TurnStartedResult(), deadline.Token);
        NotifyTurnStarted(server);
        await turnStarted.Task.WaitAsync(deadline.Token);

        var send = steering.SteerAsync("cancelled", "resolve on stop", deadline.Token);
        var request = await server.ExpectRequestAsync("turn/steer", deadline.Token);
        (await send).ShouldBe(CodexSteerResult.Sent);
        await runCancellation.CancelAsync();

        (await outcome.Task.WaitAsync(deadline.Token)).ShouldBe(CodexSteerOutcome.NotConsumed);
        var interrupt = await server.ExpectRequestAsync("turn/interrupt", deadline.Token);
        interrupt["params"]!["threadId"]!.GetValue<string>().ShouldBe(ThreadId);
        interrupt["params"]!["turnId"]!.GetValue<string>().ShouldBe(TurnId);
        server.Send(new JsonObject { ["id"] = interrupt["id"]!.DeepClone(), ["result"] = new JsonObject() });
        await Should.ThrowAsync<OperationCanceledException>(async () => await eventsTask);

        server.Send(new JsonObject
        {
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = new JsonObject { ["turnId"] = TurnId }
        });
        outcome.Task.Result.ShouldBe(CodexSteerOutcome.NotConsumed);
    }

    [Test]
    public async Task A_closed_connection_resolves_pending_steers_as_not_consumed()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        await using var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var steering = new CodexRunSteering();
        var outcome = new TaskCompletionSource<CodexSteerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) => outcome.TrySetResult(args.Outcome);
        AttachSteering(client, steering, TimeSpan.FromSeconds(5));
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsTask = StartTurn(client, server, cts.Token, turnStarted: turnStarted);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        NotifyTurnStarted(server);
        await turnStarted.Task.WaitAsync(cts.Token);

        var send = steering.SteerAsync("connection-close", "runtime exits", cts.Token);
        await server.ExpectRequestAsync("turn/steer", cts.Token);
        (await send).ShouldBe(CodexSteerResult.Sent);
        server.Close();

        (await outcome.Task.WaitAsync(cts.Token)).ShouldBe(CodexSteerOutcome.NotConsumed);
        await Should.ThrowAsync<IOException>(async () => await eventsTask);
    }

    [Test]
    public async Task Disposing_the_client_resolves_pending_steers_as_not_consumed()
    {
        using var cts = Deadline();
        var server = new CodexAppServerScriptedServer();
        var client = new CodexAppServerClient(server.ClientReader, server.ClientWriter, null, Timeout.InfiniteTimeSpan);
        client.Start();
        await Handshake(server, client, cts.Token);

        var steering = new CodexRunSteering();
        var outcome = new TaskCompletionSource<CodexSteerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        steering.OutcomeObserved += (_, args) => outcome.TrySetResult(args.Outcome);
        AttachSteering(client, steering, TimeSpan.FromSeconds(5));
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var eventsTask = StartTurn(client, server, cts.Token, turnStarted: turnStarted);
        await server.AnswerAsync("turn/start", TurnStartedResult(), cts.Token);
        NotifyTurnStarted(server);
        await turnStarted.Task.WaitAsync(cts.Token);

        var send = steering.SteerAsync("teardown", "connection is closing", cts.Token);
        await server.ExpectRequestAsync("turn/steer", cts.Token);
        (await send).ShouldBe(CodexSteerResult.Sent);

        var disposal = client.DisposeAsync().AsTask();
        (await outcome.Task.WaitAsync(cts.Token)).ShouldBe(CodexSteerOutcome.NotConsumed);
        server.Close();
        await disposal;
        await Should.ThrowAsync<IOException>(async () => await eventsTask);
    }

    [Test]
    public void Steer_delivery_timeout_has_a_finite_default()
    {
        CodexSessionOptions.DefaultSteerDeliveryTimeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Test]
    public void BuildEnvironment_applies_the_override_and_the_originator_and_endpoint()
    {
        var environment = CodexAppServerSession.BuildEnvironment(
            new Dictionary<string, string> { ["HOME"] = "/home/test" },
            baseUrl: "https://example.test/v1",
            apiKey: "key-1");

        environment["HOME"].ShouldBe("/home/test");
        environment["CODEX_INTERNAL_ORIGINATOR_OVERRIDE"].ShouldBe("codex_sdk_csharp");
        environment["OPENAI_BASE_URL"].ShouldBe("https://example.test/v1");
        environment["CODEX_API_KEY"].ShouldBe("key-1");
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(30));

    private static async Task<CodexUserQuestionResponse?> WaitForever(CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        return null;
    }

    private static OpenQuestionHandler OpenQuestion() => new();

    /// <summary>A handler that never answers and reports when a question has been handed to it.</summary>
    private sealed class OpenQuestionHandler
    {
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CodexUserInputHandler Handler => async (_, token) =>
        {
            Opened.TrySetResult();
            return await WaitForever(token).ConfigureAwait(false);
        };
    }

    private static async Task Handshake(CodexAppServerScriptedServer server, CodexAppServerClient client, CancellationToken cancellationToken)
    {
        var initialize = client.InitializeAsync(cancellationToken);
        await server.AnswerAsync("initialize", new JsonObject { ["userAgent"] = "scripted/0.163.0" }, cancellationToken);
        await server.ExpectNotificationAsync("initialized", cancellationToken);
        (await initialize).ShouldBe("scripted/0.163.0");

        var feature = client.HasExperimentalFeatureAsync(FeatureName, cancellationToken);
        await server.AnswerAsync("experimentalFeature/list", new JsonObject
        {
            ["data"] = new JsonArray(new JsonObject { ["name"] = FeatureName }),
            ["nextCursor"] = null
        }, cancellationToken);
        (await feature).ShouldBeTrue();
    }

    private static Task<List<ThreadEvent>> StartTurn(
        CodexAppServerClient client,
        CodexAppServerScriptedServer server,
        CancellationToken cancellationToken,
        string prompt = "Ask me.",
        TaskCompletionSource? turnStarted = null)
    {
        // The turn/start request goes out when the enumeration starts, so the collection runs on its own task.
        var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = prompt, ["text_elements"] = new JsonArray() });
        var turnParameters = new JsonObject { ["effort"] = "medium" };
        return CollectAsync(client.RunTurnAsync(ThreadId, input, turnParameters, cancellationToken), turnStarted);
    }

    private static void AttachSteering(
        CodexAppServerClient client,
        CodexRunSteering steering,
        TimeSpan deliveryTimeout)
    {
        steering.Attach((steerId, text, token) =>
            client.TrySteerAsync(steerId, ThreadId, text, deliveryTimeout, steering, token));
    }

    private static void NotifyTurnStarted(CodexAppServerScriptedServer server)
    {
        server.Notify("turn/started", new JsonObject
        {
            ["threadId"] = ThreadId,
            ["turn"] = new JsonObject { ["id"] = TurnId, ["status"] = "inProgress", ["items"] = new JsonArray() }
        });
    }

    private static async Task<List<ThreadEvent>> CollectAsync(
        IAsyncEnumerable<ThreadEvent> source,
        TaskCompletionSource? turnStarted = null)
    {
        var events = new List<ThreadEvent>();
        await foreach (var threadEvent in source)
        {
            events.Add(threadEvent);
            if (threadEvent is TurnStartedEvent)
            {
                turnStarted?.TrySetResult();
            }
        }

        return events;
    }

    private static JsonObject TurnStartedResult() => new()
    {
        ["turn"] = new JsonObject { ["id"] = TurnId, ["status"] = "inProgress", ["items"] = new JsonArray() }
    };

    private static JsonObject TurnCompleted(string status) => new()
    {
        ["threadId"] = ThreadId,
        ["turn"] = new JsonObject
        {
            ["id"] = TurnId,
            ["status"] = status,
            ["items"] = new JsonArray(),
            ["error"] = status == "completed" ? null : new JsonObject { ["message"] = "stopped" }
        }
    };

    private static JsonObject AgentMessage(string id, string text) => new()
    {
        ["threadId"] = ThreadId,
        ["turnId"] = TurnId,
        ["item"] = new JsonObject { ["type"] = "agentMessage", ["id"] = id, ["text"] = text }
    };

    private static JsonObject TokenUsage(long input, long cached, long output) => new()
    {
        ["threadId"] = ThreadId,
        ["turnId"] = TurnId,
        ["tokenUsage"] = new JsonObject
        {
            ["last"] = new JsonObject
            {
                ["inputTokens"] = input,
                ["cachedInputTokens"] = cached,
                ["outputTokens"] = output,
                ["reasoningOutputTokens"] = 0,
                ["totalTokens"] = input + output
            },
            ["total"] = new JsonObject
            {
                ["inputTokens"] = input,
                ["cachedInputTokens"] = cached,
                ["outputTokens"] = output,
                ["reasoningOutputTokens"] = 0,
                ["totalTokens"] = input + output
            }
        }
    };

    private static JsonObject QuestionParams(bool isBlocking) => new()
    {
        ["threadId"] = ThreadId,
        ["turnId"] = TurnId,
        ["itemId"] = "item-1",
        ["isBlocking"] = isBlocking,
        ["questions"] = new JsonArray(new JsonObject
        {
            ["id"] = "color",
            ["header"] = "Color",
            ["question"] = "Which color?",
            ["isOther"] = false,
            ["isSecret"] = false,
            ["options"] = new JsonArray(
                new JsonObject { ["label"] = "red", ["description"] = "Warm" },
                new JsonObject { ["label"] = "blue", ["description"] = "Cool" })
        })
    };

    private static JsonObject LoadPinnedSubset()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codex", "AppServer", "codex-app-server-0.163.0.subset.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }
}
