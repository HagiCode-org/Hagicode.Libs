// Feature 2A.4 `ask_user_question`: can the CLI ask a question mid-turn and accept the answer over the wire.
//
// The question agy really sends is an `ask_question` tool call (recorded-ask-*.jsonl, agy 1.5.17); the recorded test below
// replays it. The other hermetic tests use synthetic frames. The method used by the unknown-request tests is a placeholder
// ("_probe/ask_user_question") that the scripted agent sends as an agent-to-client request. Those tests assert only what
// HagiCode does with any agent request it does not know: the request is answered with JSON-RPC -32601, the turn carries on,
// and nothing reaches the caller as a question. That is the current state, and it is the gap the matrix records.
// REAL test (Real_ prefix): needs HAGICODE_REAL_CLI_TESTS=1 and agy on PATH, skips with a reason otherwise. On agy 1.5.17 the
// question reaches the client as an ask_question tool call or as text, but the turn does not wait for an answer (the reply
// is the next prompt). See docs/antigravity-feature-matrix.md.
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravityAskUserQuestionFeatureTests
{
    private const string PlaceholderQuestionMethod = "_probe/ask_user_question";

    [Test]
    public async Task Unknown_agent_request_mid_turn_is_answered_with_method_not_supported_and_the_turn_continues()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(
            QuestionRequestFrame("q-1"),
            AntigravityScriptedFrames.Chunk("Thanks, continuing.")));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "ask me something"));

        var answer = agent.SentFrames.Single(frame => frame["id"]?.ToJsonString() == "\"q-1\"" && frame["method"] is null);
        answer["error"]!["code"]!.GetValue<int>().ShouldBe(-32601);
        answer["error"]!["message"]!.GetValue<string>().ShouldContain(PlaceholderQuestionMethod);
        AntigravityTestHelpers.TextOf(messages).ShouldBe("Thanks, continuing.");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task Unknown_agent_request_is_not_surfaced_to_the_caller_as_a_question()
    {
        // Gap, recorded in the matrix: only session/request_permission is published to the notification stream.
        // Any other request, including a future question method, is dropped after the -32601 answer.
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(QuestionRequestFrame("q-2")));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "ask me something"));

        messages.Any(message => message.Type == "session.notification").ShouldBeFalse();
        messages.Any(message => message.Type.Contains("question", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Test]
    public async Task Ask_question_tool_call_is_surfaced_as_a_user_question_message_in_stream_order()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(
            AskQuestionFrame("call-1", "Which colour?", "Blue", "Green"),
            AntigravityScriptedFrames.Chunk("Waiting.")));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "ask me"));

        var questionMessage = messages.Single(message => message.Type == AntigravityUserInputRequest.MessageType);
        AntigravityUserInputRequest.TryFromCliMessage(questionMessage, out var request).ShouldBeTrue();
        request.ToolCallId.ShouldBe("call-1");
        request.Questions.Single().Question.ShouldBe("Which colour?");
        request.Questions.Single().Options.Select(option => option.Label).ShouldBe(new[] { "Blue", "Green" });
        messages.Any(message => message.Type == "tool.call").ShouldBeTrue("the tool call itself is still passed on");
        messages[^1].Type.ShouldBe("terminal.completed");
        agent.RequestsFor("session/prompt").Count.ShouldBe(1, "with no handler nothing is answered");
    }

    [Test]
    public async Task Answer_is_sent_as_the_next_prompt_on_the_same_session_after_the_asking_turn_ends()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(
            AntigravityTurnScript.EndTurn(AskQuestionFrame("call-2", "Which colour?", "Blue", "Green")),
            AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("Blue it is.")));
        var asked = new List<string>();
        var options = new AntigravityOptions
        {
            UserInputHandler = (request, _) =>
            {
                asked.Add(request.ToolCallId);
                return Task.FromResult<AntigravityUserInputResponse?>(new AntigravityUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { ["Which colour?"] = ["Blue"] }));
            }
        };
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(options, "ask me"));

        asked.ShouldBe(new[] { "call-2" });
        var prompts = agent.RequestsFor("session/prompt");
        prompts.Count.ShouldBe(2);
        prompts.Select(prompt => prompt["params"]!["sessionId"]!.GetValue<string>()).Distinct().Count().ShouldBe(1);
        prompts[1]["params"]!["prompt"]![0]!["text"]!.GetValue<string>().ShouldContain("- Which colour?: Blue");
        AntigravityTestHelpers.TextOf(messages).ShouldContain("Blue it is.");
        messages.Count(message => message.Type == "terminal.completed").ShouldBe(1);
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task Recorded_ask_question_turn_from_agy_is_answered_as_the_next_prompt_on_the_same_session()
    {
        // Recorded from agy 1.5.17 (Fixtures/Antigravity/recorded-ask-question-turn.jsonl and recorded-ask-answer-turn.jsonl).
        // The asking turn carries an ask_question tool call with its options in rawInput, and the question again as text.
        var agent = new AntigravityScriptedAgent().Enqueue(
            AntigravityTurnScript.FromFixture("recorded-ask-question-turn.jsonl"),
            AntigravityTurnScript.FromFixture("recorded-ask-answer-turn.jsonl"));
        var asked = new List<AntigravityUserInputRequest>();
        var options = new AntigravityOptions
        {
            UserInputHandler = (request, _) =>
            {
                asked.Add(request);
                return Task.FromResult<AntigravityUserInputResponse?>(new AntigravityUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { ["What is your favourite colour?"] = ["Blue"] }));
            }
        };
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            options,
            "Before you answer, ask me one clarifying question about my favourite colour and wait for my reply."));

        asked.Count.ShouldBe(1);
        asked[0].ToolCallId.ShouldBe("call_6658954896049957");
        asked[0].Questions.Single().Question.ShouldBe("What is your favourite colour?");
        asked[0].Questions.Single().Options.Select(option => option.Label).ShouldBe(new[] { "Blue", "Green", "Red", "Purple" });
        var prompts = agent.RequestsFor("session/prompt");
        prompts.Count.ShouldBe(2);
        prompts.Select(prompt => prompt["params"]!["sessionId"]!.GetValue<string>()).Distinct().Count().ShouldBe(1);
        prompts[1]["params"]!["prompt"]![0]!["text"]!.GetValue<string>().ShouldContain("- What is your favourite colour?: Blue");
        AntigravityTestHelpers.TextOf(messages).ShouldContain("Got it");
        messages.Count(message => message.Type == "terminal.completed").ShouldBe(1);
    }

    [Test]
    public async Task Question_the_handler_declines_ends_the_run_with_the_asking_turn()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(
            AntigravityTurnScript.EndTurn(AskQuestionFrame("call-3", "Which colour?", "Blue")));
        var options = new AntigravityOptions
        {
            UserInputHandler = (_, _) => Task.FromResult<AntigravityUserInputResponse?>(null)
        };
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(options, "ask me"));

        agent.RequestsFor("session/prompt").Count.ShouldBe(1);
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task Question_whose_handler_throws_is_declined_and_the_run_ends_with_the_asking_turn()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(
            AntigravityTurnScript.EndTurn(AskQuestionFrame("call-4", "Which colour?", "Blue")));
        var options = new AntigravityOptions
        {
            UserInputHandler = (_, _) => throw new InvalidOperationException("no interactive surface")
        };
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(options, "ask me"));

        agent.RequestsFor("session/prompt").Count.ShouldBe(1);
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task Question_written_only_as_agent_text_is_not_reported_as_a_user_question()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(
            AntigravityTurnScript.EndTurn(AntigravityScriptedFrames.Chunk("Which colour do you like?")));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "ask me"));

        messages.Any(message => message.Type == AntigravityUserInputRequest.MessageType).ShouldBeFalse();
        AntigravityTestHelpers.TextOf(messages).ShouldContain("Which colour do you like?");
    }

    private static string AskQuestionFrame(string toolCallId, string question, params string[] options) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "session/update",
            @params = new
            {
                sessionId = AntigravityFixtures.SessionId,
                update = new
                {
                    sessionUpdate = "tool_call",
                    toolCallId,
                    name = "ask_question",
                    status = "in_progress",
                    kind = "other",
                    title = "Run ask_question",
                    rawInput = new
                    {
                        questions = new[] { new { is_multi_select = false, options, question } }
                    }
                }
            }
        });

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_agent_asks_a_question_and_the_client_sees_it()
    {
        // Observed on agy 1.5.17 (2026-11-25): the question reaches the client either as a tool_call named
        // "ask_question" (options in rawInput) or as agent message text. The turn then ends with end_turn; agy sends no
        // request that waits for an answer, so the reply comes as the next prompt (see the feature matrix, Probes).
        var agyPath = AntigravityRealCli.RequireAgy();
        var workingDirectory = Directory.GetCurrentDirectory();
        await using var client = AntigravityRealCli.CreateSessionClient(agyPath, workingDirectory);
        await client.ConnectAsync();
        await client.InitializeAsync();
        var sessionId = (await client.StartSessionAsync(workingDirectory, null, null)).SessionId;

        var seen = new List<string>();
        using var collectionCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var collector = Task.Run(async () =>
        {
            try
            {
                await foreach (var notification in client.ReceiveNotificationsAsync(collectionCancellation.Token))
                {
                    if (notification.Parameters.ValueKind != System.Text.Json.JsonValueKind.Object ||
                        !notification.Parameters.TryGetProperty("update", out var update) ||
                        update.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        continue;
                    }

                    var kind = update.TryGetProperty("sessionUpdate", out var sessionUpdate) ? sessionUpdate.GetString() : null;
                    var toolName = update.TryGetProperty("name", out var name) ? name.GetString() : null;
                    var text = update.TryGetProperty("content", out var content) &&
                               content.ValueKind == System.Text.Json.JsonValueKind.Object &&
                               content.TryGetProperty("text", out var textValue)
                        ? textValue.GetString()
                        : null;
                    lock (seen)
                    {
                        seen.Add($"{notification.Method}:{kind}");
                        if (toolName is not null)
                        {
                            seen.Add($"tool:{toolName}");
                        }

                        if (text is not null)
                        {
                            seen.Add($"text:{text}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        await client.SendPromptAsync(
            sessionId,
            "Before you answer, ask me one clarifying question about my favourite colour and wait for my reply.");
        await Task.Delay(TimeSpan.FromSeconds(1));
        await collectionCancellation.CancelAsync();
        await collector;

        string[] snapshot;
        lock (seen)
        {
            snapshot = seen.ToArray();
        }

        var questionAsToolCall = snapshot.Any(entry => entry == "tool:ask_question");
        var questionAsText = snapshot.Any(entry => entry.StartsWith("text:", StringComparison.Ordinal) && entry.Contains('?'));
        (questionAsToolCall || questionAsText).ShouldBeTrue(
            "no question reached the client as an ask_question tool call or as agent text. Frames seen: " +
            string.Join(", ", snapshot));
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_provider_answers_a_question_as_the_next_prompt()
    {
        // Through the production provider: the question must come back as user.question, the handler must be called once the
        // asking turn ends, and the answer must be a second prompt on the same session that completes the run.
        var agyPath = AntigravityRealCli.RequireAgy();
        var provider = AntigravityRealCli.CreateProvider();
        var asked = new List<AntigravityUserInputRequest>();
        var options = new AntigravityOptions
        {
            ExecutablePath = agyPath,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UserInputHandler = (request, _) =>
            {
                lock (asked)
                {
                    asked.Add(request);
                }

                var answers = request.Questions.ToDictionary(
                    question => question.Question,
                    question => (IReadOnlyList<string>)["Blue"]);
                return Task.FromResult<AntigravityUserInputResponse?>(new AntigravityUserInputResponse(answers));
            },
            QuestionTimeout = TimeSpan.FromMinutes(2)
        };

        using var runCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            options,
            "Before you answer, ask me one clarifying question about my favourite colour and wait for my reply. Then reply with one sentence that names the colour I gave you.",
            runCancellation.Token));

        List<AntigravityUserInputRequest> seen;
        lock (asked)
        {
            seen = asked.ToList();
        }

        seen.Count.ShouldBeGreaterThan(0, "agy must ask the question as an ask_question tool call; frames: " +
            string.Join(", ", messages.Select(message => message.Type)));
        messages[^1].Type.ShouldBe("terminal.completed");
        AntigravityTestHelpers.TextOf(messages).ToLowerInvariant().ShouldContain("blue");
    }

    private static string QuestionRequestFrame(string id) =>
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = PlaceholderQuestionMethod,
            ["params"] = new JsonObject
            {
                ["sessionId"] = AntigravityFixtures.SessionId,
                ["question"] = "Which colour?"
            }
        }.ToJsonString();
}
