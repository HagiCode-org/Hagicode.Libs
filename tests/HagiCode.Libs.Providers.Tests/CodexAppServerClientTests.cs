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
/// checked against the pinned codex-cli 0.163.0 subset in Fixtures/Codex/AppServer.
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

    private static Task<List<ThreadEvent>> StartTurn(CodexAppServerClient client, CodexAppServerScriptedServer server, CancellationToken cancellationToken)
    {
        // The turn/start request goes out when the enumeration starts, so the collection runs on its own task.
        var input = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Ask me.", ["text_elements"] = new JsonArray() });
        var turnParameters = new JsonObject { ["effort"] = "medium" };
        return CollectAsync(client.RunTurnAsync(ThreadId, input, turnParameters, cancellationToken));
    }

    private static async Task<List<ThreadEvent>> CollectAsync(IAsyncEnumerable<ThreadEvent> source)
    {
        var events = new List<ThreadEvent>();
        await foreach (var threadEvent in source)
        {
            events.Add(threadEvent);
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
