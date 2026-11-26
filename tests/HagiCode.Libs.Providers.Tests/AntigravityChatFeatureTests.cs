// Feature 2A.1 `chat`: streamed text, tool calls, completion, the error and cancellation paths, and the license reply.
//
// HERMETIC tests (no suffix in the name): run on the scripted ACP agent in AntigravityScriptedAgent.cs. The fixtures named
// recorded-* under Fixtures/Antigravity/ were captured from agy 1.5.17 (provenance "recorded"); the other turn fixtures are
// SYNTHETIC (provenance "synthetic"), written from the ACP shapes, and cover the error, cancellation and license paths.
// REAL tests (Real_ prefix): run only with HAGICODE_REAL_CLI_TESTS=1 and agy on PATH, and skip with a reason otherwise.
// The license reply (#3501) was seen before 2026-11-25. On that run (agy 1.5.17) both real chat tests passed.
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravityChatFeatureTests
{
    [Test]
    public async Task Streaming_text_turn_yields_session_start_chunks_and_completion()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("chat-streaming-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "greet me"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "assistant", "assistant", "session.update", "terminal.completed"]);
        AntigravityTestHelpers.TextOf(messages).ShouldBe("Hello, there.");
        AntigravityTestHelpers.StopReasonOf(messages[^1]).ShouldBe("end_turn");
    }

    [Test]
    public async Task Tool_call_turn_streams_the_tool_call_and_its_update_before_the_reply()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("chat-tool-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "find TODO lines"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "tool.call", "tool.update", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("update").GetProperty("toolCallId").GetString().ShouldBe("antigravity-call-1");
        AntigravityTestHelpers.TextOf(messages).ShouldBe("Found 2 TODO lines.");
    }

    [Test]
    public async Task Recorded_text_turn_from_agy_streams_the_reply_and_completes()
    {
        // Recorded from agy 1.5.17 (Fixtures/Antigravity/recorded-chat-text-turn.jsonl).
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("recorded-chat-text-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "Reply with exactly the word pong."));

        AntigravityTestHelpers.TextOf(messages).ShouldBe("pong");
        messages[^1].Type.ShouldBe("terminal.completed");
        AntigravityTestHelpers.StopReasonOf(messages[^1]).ShouldBe("end_turn");
    }

    [Test]
    public async Task Recorded_tool_turn_from_agy_streams_the_tool_call_its_completion_and_the_reply()
    {
        // Recorded from agy 1.5.17 (Fixtures/Antigravity/recorded-chat-tool-turn.jsonl): a read tool call, then the answer.
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("recorded-chat-tool-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "How many lines in notes.txt contain the word TODO?"));

        var toolCalls = messages.Where(message => message.Type == "tool.call" || message.Type == "tool.update").ToList();
        toolCalls.ShouldNotBeEmpty();
        toolCalls.Select(message => message.Content.GetProperty("update").GetProperty("toolCallId").GetString()).Distinct().Count().ShouldBe(1);
        messages.Select(message => message.Type).Last().ShouldBe("terminal.completed");
        AntigravityTestHelpers.TextOf(messages).ShouldBe("2");
    }

    [Test]
    public async Task Error_stop_reason_becomes_a_terminal_failure()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("chat-error-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "fail please"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        AntigravityTestHelpers.StopReasonOf(messages[^1]).ShouldBe("error");
    }

    [Test]
    public async Task Cancelled_stop_reason_becomes_a_terminal_failure_with_the_partial_text_kept()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("chat-cancelled-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "count"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "assistant", "terminal.failed"]);
        AntigravityTestHelpers.StopReasonOf(messages[^1]).ShouldBe("cancelled");
        AntigravityTestHelpers.TextOf(messages).ShouldBe("Counting 1, 2, ");
    }

    [Test]
    public async Task Prompt_json_rpc_error_becomes_a_terminal_failure_that_names_the_code()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(new AntigravityTurnScript
        {
            Error = System.Text.Json.Nodes.JsonNode.Parse("""{"code":-32603,"message":"agent crashed"}""")
        });
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "crash"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        AntigravityTestHelpers.MessageOf(messages[^1]).ShouldContain("-32603");
        AntigravityTestHelpers.MessageOf(messages[^1]).ShouldContain("agent crashed");
    }

    // Task 1: the license reply is a failure, not assistant text. The phrase is the only rule.

    [Test]
    public async Task License_reply_split_across_chunks_becomes_a_terminal_failure_and_the_phrase_is_not_delivered_as_text()
    {
        // Known limit: the first chunk ("⚠ You do not have a valid ") is a prefix of the phrase and is streamed as assistant
        // text before the second chunk completes the match. The turn still ends in the failure, and the full phrase is never
        // delivered as text.
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("license-rejected-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "assistant", "terminal.failed"]);
        AntigravityTestHelpers.MessageOf(messages[^1]).ShouldBe(AntigravityAcpMessageMapper.LicenseRejectionMessage);
        AntigravityTestHelpers.StopReasonOf(messages[^1]).ShouldBe("error");
        AntigravityTestHelpers.TextOf(messages).ShouldNotContain("do not have a valid license");
    }

    [Test]
    public async Task License_reply_that_arrives_only_in_the_prompt_result_becomes_a_terminal_failure()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("license-rejected-result-only.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        messages.Select(message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        AntigravityTestHelpers.MessageOf(messages[^1]).ShouldBe(AntigravityAcpMessageMapper.LicenseRejectionMessage);
    }

    [Test]
    public async Task License_phrase_match_is_case_insensitive()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"antigravity-session-1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"YOU DO NOT HAVE A VALID LICENSE OF THIS PRODUCT"}}}}"""));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        messages[^1].Type.ShouldBe("terminal.failed");
        messages.Any(message => message.Type == "assistant").ShouldBeFalse();
    }

    [Test]
    public async Task Text_that_only_mentions_a_license_without_the_phrase_still_completes()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn(
            """{"jsonrpc":"2.0","method":"session/update","params":{"sessionId":"antigravity-session-1","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Please renew your license soon."}}}}"""));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        messages[^1].Type.ShouldBe("terminal.completed");
        AntigravityTestHelpers.TextOf(messages).ShouldBe("Please renew your license soon.");
    }

    [Test]
    public async Task Caller_cancellation_mid_turn_ends_the_stream_with_operation_canceled()
    {
        using var cancellation = new CancellationTokenSource();
        var agent = new AntigravityScriptedAgent().Enqueue(new AntigravityTurnScript
        {
            Frames = [AntigravityScriptedFrames.Chunk("Counting 1, ")]
        });
        var provider = new ScriptedAntigravityProvider(agent);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(new AntigravityOptions(), "count", cancellation.Token))
            {
                if (message.Type == "assistant")
                {
                    await cancellation.CancelAsync();
                }
            }
        });
    }

    [Test]
    public async Task Caller_cancellation_sends_no_session_cancel_yet_known_gap()
    {
        // Gap, recorded in the matrix (2A.8): HagiCode does not send ACP session/cancel. A cancelled turn only ends the
        // local stream. When session/cancel is added, change this test together with the matrix row.
        using var cancellation = new CancellationTokenSource();
        var agent = new AntigravityScriptedAgent().Enqueue(new AntigravityTurnScript
        {
            Frames = [AntigravityScriptedFrames.Chunk("Counting 1, ")]
        });
        var provider = new ScriptedAntigravityProvider(agent);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(new AntigravityOptions(), "count", cancellation.Token))
            {
                if (message.Type == "assistant")
                {
                    await cancellation.CancelAsync();
                }
            }
        });

        agent.SentFrames.Select(frame => frame["method"]?.GetValue<string>()).ShouldNotContain("session/cancel");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_prompt_streams_text_and_completion()
    {
        AntigravityRealCli.RequireAgy();
        var provider = AntigravityRealCli.CreateProvider();

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { WorkingDirectory = Directory.GetCurrentDirectory() },
            "Reply with exactly the word 'pong'."));

        var failureMessage = messages.Where(message => message.Type == "terminal.failed").Select(AntigravityTestHelpers.MessageOf).FirstOrDefault();
        failureMessage.ShouldBeNull();
        messages[^1].Type.ShouldBe("terminal.completed");
        AntigravityTestHelpers.TextOf(messages).ToLowerInvariant().ShouldContain("pong");
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_cancellation_mid_turn_ends_the_stream_with_operation_canceled()
    {
        AntigravityRealCli.RequireAgy();
        var provider = AntigravityRealCli.CreateProvider();
        using var cancellation = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               new AntigravityOptions { WorkingDirectory = Directory.GetCurrentDirectory() },
                               "Count from 1 to 300, one number per line.",
                               cancellation.Token))
            {
                if (message.Type == "assistant")
                {
                    await cancellation.CancelAsync();
                }
            }
        });
    }
}

/// <summary>Builders for inline synthetic frames, kept next to the test classes that use them.</summary>
internal static class AntigravityScriptedFrames
{
    public static string Chunk(string text) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "session/update",
            @params = new
            {
                sessionId = AntigravityFixtures.SessionId,
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text }
                }
            }
        });
}
