using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Reasonix;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class ReasonixProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string RealCliModelEnvironmentVariable = "HAGICODE_REAL_CLI_REASONIX_MODEL";
    private static readonly string[] ReasonixExecutableCandidates = ["reasonix"];

    [Test]
    public void BuildCommandArguments_filters_legacy_bootstrap_flags_for_reasonix_2x()
    {
        var provider = CreateProvider();

        var arguments = provider.BuildCommandArguments(new ReasonixOptions
        {
            WorkingDirectory = "/tmp/project",
            Model = "deepseek-v4-flash",
            Effort = "high",
            BudgetUsd = 1.25m,
            TranscriptPath = "/tmp/reasonix/transcript.jsonl",
            EnableYolo = true,
            McpServerSpecs = ["stdio:git", "stdio:search"],
            McpPrefix = "reasonix",
            ExtraArguments =
            [
                "acp",
                "--dir", "/tmp/ignored",
                "-m", "ignored-model",
                "-model", "ignored-provider",
                "--effort", "low",
                "--budget", "0.25",
                "--transcript", "/tmp/ignored.jsonl",
                "--mcp", "ignored",
                "--mcp-prefix", "ignored",
                "--yolo",
                "--dangerously-skip-permissions",
                "--no-proxy",
                "   "
            ]
        });

        arguments.ShouldBe(
        [
            "acp",
            "-model", "deepseek-v4-flash"
        ]);
    }

    [Test]
    public async Task ExecuteAsync_uses_custom_executable_and_streams_normalized_messages()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               ExecutablePath = "/custom/reasonix-dev",
                               WorkingDirectory = "/tmp/project",
                               Model = "deepseek-v4-flash",
                               Effort = "medium",
                               BudgetUsd = 2.5m,
                               EnableYolo = true,
                               EnvironmentVariables = new Dictionary<string, string?>
                               {
                                   ["REASONIX_TOKEN"] = "token"
                               },
                               ExtraArguments = ["--no-proxy"]
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.LastStartContext!.ExecutablePath.ShouldBe("/custom/reasonix-dev");
        provider.LastStartContext.WorkingDirectory.ShouldBe("/tmp/project");
        provider.LastStartContext.Arguments.ShouldBe(
        [
            "acp",
            "-model", "deepseek-v4-flash"
        ]);
        provider.LastStartContext.EnvironmentVariables!["REASONIX_TOKEN"].ShouldBe("token");
        provider.SessionClient!.ConnectCalls.ShouldBe(1);
        provider.SessionClient.InitializeCalls.ShouldBe(1);
        provider.SessionClient.StartSessionCalls.ShouldBe(1);
        provider.SessionClient.LastWorkingDirectory.ShouldBe("/tmp/project");
        provider.SessionClient.LastSessionId.ShouldBeNull();
        provider.SessionClient.LastModel.ShouldBeNull();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Test]
    public async Task ExecuteAsync_forwards_session_id_and_emits_resumed_lifecycle_message()
    {
        var provider = CreateProvider();
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               SessionId = "session-123",
                               Model = "deepseek-flash"
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        provider.SessionClient!.LastSessionId.ShouldBe("session-123");
        messages[0].Type.ShouldBe("session.resumed");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-123");
    }

    [Test]
    public async Task ExecuteAsync_resumed_session_skips_replayed_history_and_keeps_the_current_turn_output()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            emitReplayOnResume: true,
            replayedAssistantText: "old-response",
            currentAssistantChunkText: "new-response",
            promptOutputText: "new-response"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               SessionId = "session-123"
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.resumed", "assistant", "terminal.completed"]);

        var visibleAssistantText = string.Concat(
            messages
                .Where(static message => message.Type == "assistant")
                .Select(static message => message.Content.TryGetProperty("text", out var textElement) ? textElement.GetString() : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));

        visibleAssistantText.ShouldBe("new-response");
        visibleAssistantText.ShouldNotContain("old-response");
    }

    [Test]
    public async Task ExecuteAsync_resumed_session_uses_latest_assistant_generation_when_replay_has_no_metadata()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            emitReplayOnResume: true,
            replayedAssistantText: "old-response",
            currentAssistantChunkText: "new-response",
            promptOutputText: "new-response",
            includeThoughtChunks: true,
            includeReplayMetadata: false,
            includeCurrentAssistantMetadata: false));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               SessionId = "session-123"
                           },
                           "hello"))
        {
            messages.Add(message);
        }

        var visibleAssistantText = string.Concat(
            messages
                .Where(static message => message.Type == "assistant")
                .Select(static message => message.Content.TryGetProperty("text", out var textElement) ? textElement.GetString() : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));

        visibleAssistantText.ShouldBe("new-response");
        visibleAssistantText.ShouldNotContain("old-response");
    }

    [Test]
    public async Task ExecuteAsync_streams_reasoning_chunks_as_assistant_thought_messages()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(includeThoughtChunks: true));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new ReasonixOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant.thought", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("thinking...");
    }

    [Test]
    public async Task ExecuteAsync_falls_back_to_prompt_result_when_notification_loop_ends_early()
    {
        var provider = CreateProvider(sessionClient: new FakeAcpSessionClient(
            emitNotifications: false,
            promptStopReason: "fallback",
            promptOutputText: "pong"));
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(new ReasonixOptions(), "hello"))
        {
            messages.Add(message);
        }

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("pong");
        messages[2].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Test]
    public async Task PingAsync_reports_initialize_details_when_bootstrap_succeeds()
    {
        var provider = CreateProvider();

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.Version.ShouldContain("reasonix");
        result.Version.ShouldContain("Reasonix ACP bootstrap");
        provider.SessionClient!.InitializeCalls.ShouldBe(1);
    }

    [Test]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Test]
    public void NormalizeNotification_maps_agent_message_chunks_to_shared_assistant_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new { type = "text", text = "pong" }
                }
            }));

        var messages = ReasonixAcpMessageMapper.NormalizeNotification(notification);

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("assistant");
        messages[0].Content.GetProperty("text").GetString().ShouldBe("pong");
    }

    [Test]
    [Category("RealCli")]
    public async Task PingAsync_can_validate_installed_reasonix_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(ReasonixExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Reasonix CLI was not found on PATH even though the real CLI validation path was enabled.");
        }

        var provider = new ReasonixProvider(resolver, new CliProcessManager(), null);

        provider.IsAvailable.ShouldBeTrue();

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("reasonix");
        result.Success.ShouldBeTrue();
        result.Version.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldBeNullOrWhiteSpace();
    }

    [Test]
    [Category("RealCli")]
    public async Task ExecuteAsync_can_stream_real_reasonix_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }
        var modelRef = GetRealCliModelRef();
        if (modelRef is null)
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(ReasonixExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Reasonix CLI was not found on PATH even though the real CLI execution path was enabled.");
        }

        using var workspace = new TemporaryDirectory();
        var provider = new ReasonixProvider(resolver, new CliProcessManager(), null);
        var messages = new List<CliMessage>();

        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               WorkingDirectory = workspace.Path,
                               Model = modelRef,
                               Effort = "low",
                               EnableYolo = true,
                               StartupTimeout = TimeSpan.FromSeconds(20)
                           },
                           "Reply with exactly: PONG",
                           CancellationToken.None))
        {
            messages.Add(message);
        }

        messages.ShouldContain(static message => message.Type == "session.started");
        messages.ShouldContain(static message => message.Type == "terminal.completed");
        messages.ShouldNotContain(static message => message.Type == "terminal.failed");

        var visibleAssistantText = string.Concat(
            messages
                .Where(static message => message.Type == "assistant")
                .Select(static message => message.Content.TryGetProperty("text", out var textElement) ? textElement.GetString() : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));

        visibleAssistantText.Trim().ShouldBe("PONG");
    }

    [Test]
    [Category("RealCli")]
    public async Task ExecuteAsync_real_resumed_session_does_not_replay_previous_turn_output_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }
        var modelRef = GetRealCliModelRef();
        if (modelRef is null)
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(ReasonixExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Reasonix CLI was not found on PATH even though the real CLI execution path was enabled.");
        }

        using var workspace = new TemporaryDirectory();
        var provider = new ReasonixProvider(resolver, new CliProcessManager(), null);
        var firstToken = $"FIRST-{Guid.NewGuid():N}";
        var secondToken = $"SECOND-{Guid.NewGuid():N}";

        var firstTurnMessages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               WorkingDirectory = workspace.Path,
                               Model = modelRef,
                               StartupTimeout = TimeSpan.FromSeconds(20)
                           },
                           $"Reply with exactly: {firstToken}",
                           CancellationToken.None))
        {
            firstTurnMessages.Add(message);
        }

        var sessionId = firstTurnMessages
            .Select(static message => message.Content.TryGetProperty("session_id", out var sessionIdElement) ? sessionIdElement.GetString() : null)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        sessionId.ShouldNotBeNullOrWhiteSpace();

        var secondTurnMessages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           new ReasonixOptions
                           {
                               WorkingDirectory = workspace.Path,
                               SessionId = sessionId,
                               Model = modelRef,
                               StartupTimeout = TimeSpan.FromSeconds(20)
                           },
                           $"Reply with exactly: {secondToken}",
                           CancellationToken.None))
        {
            secondTurnMessages.Add(message);
        }

        var secondTurnAssistantText = string.Concat(
            secondTurnMessages
                .Where(static message => message.Type == "assistant")
                .Select(static message => message.Content.TryGetProperty("text", out var textElement) ? textElement.GetString() : null)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));

        secondTurnAssistantText.ShouldContain(secondToken);
        secondTurnAssistantText.ShouldNotContain(firstToken);
    }

    [Test]
    public async Task Scripted_transport_replays_the_initialize_fixture_and_reports_v2_41_0()
    {
        var provider = CreateFixtureProvider("initialize");

        var result = await provider.PingAsync();

        result.Success.ShouldBeTrue(result.ErrorMessage);
        result.Version.ShouldNotBeNull().ShouldContain("v2.41.0");
        provider.Transports.Single().SentMethods.ShouldBe(["initialize"]);
    }

    [Test]
    public async Task Scripted_transport_never_sends_session_set_model_for_reasonix()
    {
        var provider = CreateFixtureProvider("text-turn");

        await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { Model = "fixture-provider/model-01" },
            "Reply with the single word pong."));

        var methods = provider.Transports.Single().SentMethods;
        methods.ShouldContain("session/prompt");
        methods.ShouldNotContain("session/set_model");
    }

    [Test]
    public async Task ExecuteAsync_resumes_with_session_resume_when_advertised_and_replays_nothing()
    {
        var provider = CreateFixtureProvider("resume-replay");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { SessionId = "fixture-session-01" },
            "What first word did you read from probe.txt earlier? Reply with that word only."));

        var methods = provider.Transports.Single().SentMethods;
        methods.ShouldContain("session/resume");
        methods.ShouldNotContain("session/load");
        messages[0].Type.ShouldBe("session.resumed");
        AssistantText(messages).ShouldBe("probe");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_load_replay_without_meta_yields_no_replayed_user_text_or_tool_calls()
    {
        var provider = CreateFixtureProvider("load-replay", removeResumeCapability: true);

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { SessionId = "fixture-session-01" },
            "What first word did you read from probe.txt earlier? Reply with that word only."));

        provider.Transports.Single().SentMethods.ShouldContain("session/load");
        messages.Select(static message => message.Type).ShouldNotContain("user_message_chunk");
        messages.Where(static message => message.Type is "tool.call" or "tool.update").ShouldBeEmpty();
        string.Join("\n", messages.Select(static message => message.Content.GetRawText()))
            .ShouldNotContain("Read the file probe.txt in the current directory");
        AssistantText(messages).ShouldBe("probe");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_live_pending_to_completed_tool_call_is_still_emitted_for_a_new_session()
    {
        var provider = CreateFixtureProvider("tool-turn");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions(),
            "Read the file probe.txt in the current directory and reply with its first word only."));

        messages.Select(static message => message.Type).ShouldContain("tool.call");
        messages.Select(static message => message.Type).ShouldContain("tool.update");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_unknown_model_yields_terminal_failure_with_failure_kind_and_releases_the_process()
    {
        var provider = CreateFixtureProvider("unknown-model");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { Model = "fixture-provider/unknown-model" },
            "hello"));

        var failure = messages.ShouldHaveSingleItem();
        failure.Type.ShouldBe("terminal.failed");
        failure.Content.GetProperty("failure_kind").GetString().ShouldBe("unknown_model");
        failure.Content.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("fixture-provider/unknown-model");
        provider.Transports.Single().IsTerminated.ShouldBeTrue();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExecuteAsync_unknown_session_yields_failure_kind_unknown_session(bool loadOnly)
    {
        var provider = CreateFixtureProvider("unknown-session", removeResumeCapability: loadOnly);

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { SessionId = "fixture-session-01" },
            "hello"));

        var failure = messages.ShouldHaveSingleItem();
        failure.Type.ShouldBe("terminal.failed");
        failure.Content.GetProperty("failure_kind").GetString().ShouldBe("unknown_session");
        failure.Content.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("fixture-session-01");
        provider.Transports.Single().IsTerminated.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteAsync_enable_yolo_sends_tool_approval_yolo_when_the_option_is_advertised()
    {
        var provider = CreateFixtureProvider("set-config-option-yolo");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { EnableYolo = true },
            "Run the shell command `echo yolo-probe` and reply with its output only."));

        var configCall = provider.Transports.Single().SentMessages
            .Single(static message => message["method"]?.GetValue<string>() == "session/set_config_option");
        configCall["params"]!["configId"]!.GetValue<string>().ShouldBe("tool_approval");
        configCall["params"]!["value"]!.GetValue<string>().ShouldBe("yolo");
        configCall["params"]!["sessionId"]!.GetValue<string>().ShouldBe("fixture-session-01");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_enable_yolo_without_the_advertised_option_skips_the_call_and_runs_the_turn()
    {
        var provider = CreateFixtureProvider("set-config-option-yolo", removeToolApprovalOption: true);

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { EnableYolo = true },
            "Run the shell command `echo yolo-probe` and reply with its output only."));

        var methods = provider.Transports.Single().SentMethods;
        methods.ShouldNotContain("session/set_config_option");
        methods.ShouldContain("session/prompt");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_enable_yolo_failed_call_is_ignored_and_the_turn_runs()
    {
        var provider = CreateFixtureProvider("set-config-option-yolo", failingMethods: ["session/set_config_option"]);

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions { EnableYolo = true },
            "Run the shell command `echo yolo-probe` and reply with its output only."));

        var methods = provider.Transports.Single().SentMethods;
        methods.ShouldContain("session/set_config_option");
        methods.ShouldContain("session/prompt");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_cancelled_stop_reason_ends_the_turn_as_a_terminal_failure()
    {
        var provider = CreateFixtureProvider("cancel-mid-turn");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions(),
            "Write a numbered list of 200 short facts about the history of the abacus, one per line."));

        var terminal = messages[^1];
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("cancelled");
    }

    [Test]
    public async Task ExecuteAsync_new_session_reports_started_then_streams_text_and_completes()
    {
        var provider = CreateFixtureProvider("text-turn");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions(),
            "Reply with the single word pong."));

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("fixture-session-01");
        AssistantText(messages).ShouldBe("pong");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_allowed_permission_is_answered_and_the_gated_tools_complete()
    {
        var provider = CreateFixtureProvider("permission-turn");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions(),
            "Create a file named permission-probe.txt in the current directory containing the word ok, then reply with done."));

        var transport = provider.Transports.Single();
        transport.SentMessages
            .Where(static message => message["result"]?["outcome"]?["outcome"]?.GetValue<string>() == "selected")
            .ShouldNotBeEmpty();
        messages.Where(static message => message.Type == "tool.update")
            .All(static message => message.Content.GetProperty("update").GetProperty("status").GetString() == "completed")
            .ShouldBeTrue();
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_rejected_permission_fails_the_tool_and_the_turn_still_completes()
    {
        var provider = CreateFixtureProvider("permission-reject-once");

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions(),
            "Create a file named reject-probe.txt in the current directory containing the word ok, then reply with done."));

        var toolUpdate = messages.Last(static message => message.Type == "tool.update");
        toolUpdate.Content.GetProperty("update").GetProperty("status").GetString().ShouldBe("failed");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [TestCase("usage_update")]
    [TestCase("available_commands_update")]
    [TestCase("config_option_update")]
    public void NormalizeNotification_housekeeping_updates_produce_no_assistant_text_or_tool_call(string updateKind)
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new { sessionUpdate = updateKind, used = 10, size = 200000 }
            }));

        var messages = ReasonixAcpMessageMapper.NormalizeNotification(notification);

        messages.Where(static message => message.Type is "assistant" or "tool.call" or "tool.update").ShouldBeEmpty();
    }

    [TestCase("_reasonix.io/session/status_update")]
    [TestCase("_reasonix.io/session/mcpStatus_update")]
    public void NormalizeNotification_reasonix_session_extension_produces_no_assistant_text_or_tool_call(string method)
    {
        var notification = new AcpNotification(
            method,
            JsonSerializer.SerializeToElement(new { sessionId = "session-1", schemaVersion = 1 }));

        var messages = ReasonixAcpMessageMapper.NormalizeNotification(notification);

        messages.Where(static message => message.Type is "assistant" or "tool.call" or "tool.update").ShouldBeEmpty();
    }

    [Test]
    public void NormalizeNotification_user_message_chunk_is_replay_only_and_produces_no_message()
    {
        var notification = new AcpNotification(
            "session/update",
            JsonSerializer.SerializeToElement(new
            {
                sessionId = "session-1",
                update = new
                {
                    sessionUpdate = "user_message_chunk",
                    content = new { type = "text", text = "replayed prompt" }
                }
            }));

        ReasonixAcpMessageMapper.NormalizeNotification(notification).ShouldBeEmpty();
    }

    private static async Task<List<CliMessage>> CollectAsync(IAsyncEnumerable<CliMessage> messages)
    {
        var collected = new List<CliMessage>();
        await foreach (var message in messages)
        {
            collected.Add(message);
        }

        return collected;
    }

    private static string AssistantText(IEnumerable<CliMessage> messages)
    {
        return string.Concat(messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.TryGetProperty("text", out var text) ? text.GetString() : null));
    }

    private static ReasonixFixtureProvider CreateFixtureProvider(
        string transcriptName,
        bool removeResumeCapability = false,
        bool removeToolApprovalOption = false,
        string[]? failingMethods = null)
    {
        void MutateResponse(string method, JsonObject frame)
        {
            if (removeResumeCapability && method == "initialize" &&
                frame["result"]?["agentCapabilities"]?["sessionCapabilities"] is JsonObject capabilities)
            {
                capabilities.Remove("resume");
            }

            if (removeToolApprovalOption && method == "session/new" &&
                frame["result"]?["configOptions"] is JsonArray configOptions)
            {
                for (var index = configOptions.Count - 1; index >= 0; index--)
                {
                    if (configOptions[index]?["id"]?.GetValue<string>() == "tool_approval")
                    {
                        configOptions.RemoveAt(index);
                    }
                }
            }
        }

        return new ReasonixFixtureProvider(transcriptName, MutateResponse, failingMethods);
    }

    private sealed class ReasonixFixtureProvider(
        string transcriptName,
        Action<string, JsonObject> mutateResponse,
        IEnumerable<string>? failingMethods)
        : ReasonixProvider(new StubExecutableResolver(), new CliProcessManager(), new StubRuntimeEnvironmentResolver())
    {
        public List<ReasonixScriptedAcpTransport> Transports { get; } = [];

        protected override IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
        {
            var transport = new ReasonixScriptedAcpTransport(
                ReasonixTranscriptFixtures.Load(transcriptName),
                mutateResponse,
                failingMethods);
            Transports.Add(transport);
            return transport;
        }
    }

    private static TestReasonixProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        FakeAcpSessionClient? sessionClient = null)
    {
        return new TestReasonixProvider(
            executableResolver ?? new StubExecutableResolver(),
            new CliProcessManager(),
            new StubRuntimeEnvironmentResolver(),
            sessionClient ?? new FakeAcpSessionClient());
    }

    [Test]
    [Category("RealCli")]
    public async Task ExecuteAsync_real_resume_skips_replay_and_keeps_the_resumed_model_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var modelRef = GetRealCliModelRef();
        if (modelRef is null)
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        using var workspace = new TemporaryDirectory();
        var firstTurnProvider = new RecordingReasonixProvider(resolver, new CliProcessManager());
        var firstToken = $"FIRST-{Guid.NewGuid():N}";
        var secondToken = $"SECOND-{Guid.NewGuid():N}";

        var firstTurnMessages = await CollectAsync(firstTurnProvider.ExecuteAsync(
            new ReasonixOptions
            {
                WorkingDirectory = workspace.Path,
                Model = modelRef,
                StartupTimeout = TimeSpan.FromSeconds(20)
            },
            $"Reply with exactly: {firstToken}"));

        var sessionId = firstTurnMessages
            .Select(static message => message.Content.TryGetProperty("session_id", out var sessionIdElement) ? sessionIdElement.GetString() : null)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
        sessionId.ShouldNotBeNullOrWhiteSpace();

        var resumedProvider = new RecordingReasonixProvider(resolver, new CliProcessManager());
        var secondTurnMessages = await CollectAsync(resumedProvider.ExecuteAsync(
            new ReasonixOptions
            {
                WorkingDirectory = workspace.Path,
                SessionId = sessionId,
                Model = modelRef,
                StartupTimeout = TimeSpan.FromSeconds(20)
            },
            $"Reply with exactly: {secondToken}"));

        var transport = resumedProvider.Transports.Single();
        var sentMethods = transport.Sent.Select(static message => message["method"]?.GetValue<string>()).ToList();
        sentMethods.ShouldContain("session/resume");
        sentMethods.ShouldNotContain("session/load");

        var resumeRequestId = transport.Sent
            .Single(static message => message["method"]?.GetValue<string>() == "session/resume")["id"]!
            .ToJsonString();
        var resumeResult = transport.Received
            .Single(message => message["id"]?.ToJsonString() == resumeRequestId && message["result"] is not null)["result"]!;
        resumeResult["models"]!["currentModelId"]!.GetValue<string>().ShouldBe(modelRef);

        secondTurnMessages[0].Type.ShouldBe("session.resumed");
        var secondTurnText = AssistantText(secondTurnMessages);
        secondTurnText.ShouldContain(secondToken);
        secondTurnText.ShouldNotContain(firstToken);
    }

    [Test]
    [Category("RealCli")]
    public async Task ExecuteAsync_real_unknown_model_yields_a_readable_terminal_failure_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        using var workspace = new TemporaryDirectory();
        var provider = new ReasonixProvider(resolver, new CliProcessManager(), null);
        var unknownModelRef = $"fixture-provider/unknown-{Guid.NewGuid():N}";

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions
            {
                WorkingDirectory = workspace.Path,
                Model = unknownModelRef,
                StartupTimeout = TimeSpan.FromSeconds(20)
            },
            "Reply with exactly: ok"));

        var failure = messages.ShouldHaveSingleItem();
        failure.Type.ShouldBe("terminal.failed");
        failure.Content.GetProperty("failure_kind").GetString().ShouldBe("unknown_model");
        failure.Content.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain(unknownModelRef);
    }

    [Test]
    [Category("RealCli")]
    public async Task ExecuteAsync_real_enable_yolo_sets_tool_approval_and_runs_without_permission_prompts_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var modelRef = GetRealCliModelRef();
        if (modelRef is null)
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        using var workspace = new TemporaryDirectory();
        var provider = new RecordingReasonixProvider(resolver, new CliProcessManager());

        var messages = await CollectAsync(provider.ExecuteAsync(
            new ReasonixOptions
            {
                WorkingDirectory = workspace.Path,
                Model = modelRef,
                EnableYolo = true,
                StartupTimeout = TimeSpan.FromSeconds(20)
            },
            "Run the shell command `echo yolo-probe` and reply with its output only."));

        var transport = provider.Transports.Single();
        var configRequest = transport.Sent.Single(static message => message["method"]?.GetValue<string>() == "session/set_config_option");
        configRequest["params"]!["configId"]!.GetValue<string>().ShouldBe("tool_approval");
        configRequest["params"]!["value"]!.GetValue<string>().ShouldBe("yolo");
        transport.Received.Count(static message => message["method"]?.GetValue<string>() == "session/request_permission").ShouldBe(0);
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    private static string? GetRealCliModelRef()
    {
        var value = Environment.GetEnvironmentVariable(RealCliModelEnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed class RecordingReasonixProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager)
        : ReasonixProvider(executableResolver, processManager, null)
    {
        public List<RecordingAcpTransport> Transports { get; } = [];

        protected override IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
        {
            var transport = new RecordingAcpTransport(base.CreateAcpTransport(startContext));
            Transports.Add(transport);
            return transport;
        }
    }

    /// <summary>
    /// Forwards to the real subprocess transport while keeping every frame, so real-CLI tests can read raw responses.
    /// </summary>
    private sealed class RecordingAcpTransport(IAcpTransport inner) : IAcpTransport
    {
        private readonly List<JsonObject> _sent = [];
        private readonly List<JsonObject> _received = [];

        public IReadOnlyList<JsonObject> Sent => _sent;

        public IReadOnlyList<JsonObject> Received => _received;

        public bool IsConnected => inner.IsConnected;

        public Task ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

        public Task SendMessageAsync(string message, CancellationToken cancellationToken = default)
        {
            foreach (var line in message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (JsonNode.Parse(line) is JsonObject sent)
                {
                    _sent.Add(sent);
                }
            }

            return inner.SendMessageAsync(message, cancellationToken);
        }

        public async IAsyncEnumerable<string> ReceiveMessagesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var frame in inner.ReceiveMessagesAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var line in frame.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (JsonNode.Parse(line) is JsonObject received)
                    {
                        _received.Add(received);
                    }
                }

                yield return frame;
            }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestReasonixProvider(
        CliExecutableResolver executableResolver,
        CliProcessManager processManager,
        IRuntimeEnvironmentResolver runtimeEnvironmentResolver,
        FakeAcpSessionClient sessionClient)
        : ReasonixProvider(executableResolver, processManager, runtimeEnvironmentResolver)
    {
        public ProcessStartContext? LastStartContext { get; private set; }

        public FakeAcpSessionClient? SessionClient { get; private set; }

        protected override IAcpSessionClient CreateSessionClient(ProcessStartContext startContext)
        {
            LastStartContext = startContext;
            SessionClient = sessionClient;
            return sessionClient;
        }
    }

    private sealed class FakeAcpSessionClient(
        JsonElement? initializeResult = null,
        bool emitNotifications = true,
        bool includeThoughtChunks = false,
        string? promptStopReason = "end_turn",
        string promptOutputText = "pong",
        bool emitReplayOnResume = false,
        string replayedAssistantText = "old-response",
        string? currentAssistantChunkText = null,
        bool includeReplayMetadata = true,
        bool includeCurrentAssistantMetadata = true) : IAcpSessionClient
    {
        public int ConnectCalls { get; private set; }
        public int InitializeCalls { get; private set; }
        public int StartSessionCalls { get; private set; }
        public int PromptCalls { get; private set; }
        public string? LastWorkingDirectory { get; private set; }
        public string? LastSessionId { get; private set; }
        public string? LastModel { get; private set; }

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task<JsonElement> InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCalls++;
            return Task.FromResult(initializeResult ?? JsonSerializer.SerializeToElement(new
            {
                agentInfo = new { name = "reasonix", version = "1.2.0" }
            }));
        }

        public Task<JsonElement> InvokeBootstrapMethodAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
        }

        public Task<AcpSessionHandle> StartSessionAsync(
            string workingDirectory,
            string? sessionId,
            string? model,
            CancellationToken cancellationToken = default)
        {
            StartSessionCalls++;
            LastWorkingDirectory = workingDirectory;
            LastSessionId = sessionId;
            LastModel = model;

            var resolvedSessionId = string.IsNullOrWhiteSpace(sessionId) ? "session-new" : sessionId;

            return Task.FromResult(new AcpSessionHandle(
                resolvedSessionId,
                !string.IsNullOrWhiteSpace(sessionId),
                JsonSerializer.SerializeToElement(new { sessionId = resolvedSessionId })));
        }

        public Task<JsonElement> SetModeAsync(string sessionId, string modeId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(JsonSerializer.SerializeToElement(new { sessionId, modeId }));
        }

        public Task<JsonElement> SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken = default)
        {
            PromptCalls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new
            {
                stopReason = promptStopReason,
                outputText = promptOutputText
            }));
        }

        public async IAsyncEnumerable<AcpNotification> ReceiveNotificationsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (!emitNotifications)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                yield break;
            }

            var sessionId = string.IsNullOrWhiteSpace(LastSessionId) ? "session-new" : LastSessionId;

            if (emitReplayOnResume && !string.IsNullOrWhiteSpace(LastSessionId))
            {
                yield return new AcpNotification(
                    "session/update",
                    includeReplayMetadata
                        ? JsonSerializer.SerializeToElement(new
                        {
                            sessionId,
                            update = new
                            {
                                sessionUpdate = "agent_message_chunk",
                                content = new { type = "text", text = replayedAssistantText }
                            },
                            _meta = new Dictionary<string, object?>
                            {
                                ["ai-coding/streamed"] = false,
                                ["ai-coding/request-id"] = null,
                                ["ai-coding/turn-id"] = null
                            }
                        })
                        : JsonSerializer.SerializeToElement(new
                        {
                            sessionId,
                            update = new
                            {
                                sessionUpdate = "agent_message_chunk",
                                content = new { type = "text", text = replayedAssistantText }
                            }
                        }));
            }

            if (includeThoughtChunks)
            {
                yield return new AcpNotification(
                    "session/update",
                    JsonSerializer.SerializeToElement(new
                    {
                        sessionId,
                        update = new
                        {
                            sessionUpdate = "agent_thought_chunk",
                            content = new { type = "text", text = "thinking..." }
                        }
                    }));
            }

            yield return new AcpNotification(
                "session/update",
                includeCurrentAssistantMetadata
                    ? JsonSerializer.SerializeToElement(new
                    {
                        sessionId,
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new { type = "text", text = currentAssistantChunkText ?? promptOutputText }
                        },
                        _meta = new Dictionary<string, object?>
                        {
                            ["ai-coding/streamed"] = true,
                            ["ai-coding/request-id"] = "request-1",
                            ["ai-coding/turn-id"] = "turn-1"
                        }
                    })
                    : JsonSerializer.SerializeToElement(new
                    {
                        sessionId,
                        update = new
                        {
                            sessionUpdate = "agent_message_chunk",
                            content = new { type = "text", text = currentAssistantChunkText ?? promptOutputText }
                        }
                    }));

            yield return new AcpNotification(
                "session/update",
                JsonSerializer.SerializeToElement(new
                {
                    sessionId,
                    update = new
                    {
                        sessionUpdate = "prompt_completed",
                        stopReason = promptStopReason
                    }
                }));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public string? GetDiagnosticSummary() => null;
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class MissingExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => null;
    }

    private sealed class StubRuntimeEnvironmentResolver : IRuntimeEnvironmentResolver
    {
        public Task<IReadOnlyDictionary<string, string?>> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, string?>>(new Dictionary<string, string?>
            {
                ["PATH"] = "/tmp/bin"
            });
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-reasonix-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "# reasonix integration workspace");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}