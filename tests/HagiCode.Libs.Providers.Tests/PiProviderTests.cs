using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Environment;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Omp;
using HagiCode.Libs.Providers.Pi;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class PiProviderTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private static readonly string[] PiExecutableCandidates = ["pi", "pi-cli"];

    [Fact]
    public async Task ExecuteAsync_builds_expected_non_interactive_json_command()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline")
        };
        var provider = CreateProvider(processManager: processManager);

        await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                ExecutablePath = "/custom/pi",
                WorkingDirectory = "/tmp/pi-project",
                Provider = "omniroute",
                Model = "glm/glm-4.7",
                SystemPrompt = "You are a trip planner.",
                AppendSystemPrompts = ["Keep answers terse."],
                SessionId = "pi-session-1",
                DisableBuiltinTools = true,
                AllowedTools = ["read", "grep"],
                ExcludedTools = ["bash"],
                Thinking = "minimal",
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["PI_OFFLINE"] = "1"
                },
                ExtraArguments = ["--verbose", "   ", "--offline"]
            },
            "Plan a two-day trip.");

        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.ExecutablePath.ShouldBe("/custom/pi");
        processManager.LastContext.WorkingDirectory.ShouldBe("/tmp/pi-project");
        processManager.LastContext.Arguments.ShouldBe(
        [
            "--mode",
            "json",
            "--print",
            "--model",
            "omniroute/glm/glm-4.7",
            "--system-prompt",
            "You are a trip planner.",
            "--append-system-prompt",
            "Keep answers terse.",
            "--thinking",
            "minimal",
            "--session-id",
            "pi-session-1",
            "--no-builtin-tools",
            "--tools",
            "read,grep",
            "--exclude-tools",
            "bash",
            "--verbose",
            "--offline",
            "Plan a two-day trip."
        ]);
        processManager.LastContext.EnvironmentVariables!.ShouldContainKeyAndValue("PI_OFFLINE", "1");
        processManager.LastContext.EnvironmentVariables.ShouldContainKeyAndValue("PATH", "/tmp/bin");
    }

    [Fact]
    public async Task ExecuteAsync_uses_three_segment_selector_for_omniroute_models()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("ok")
        };
        var provider = CreateProvider(processManager: processManager);

        await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                Provider = "omniroute",
                Model = "omniroute/ds/deepseek-v4-flash",
                NoSession = true
            },
            "hello");

        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.Arguments.ShouldNotContain("--provider");
        processManager.LastContext.Arguments.ShouldContain("--model");
        processManager.LastContext.Arguments.ShouldContain("omniroute/ds/deepseek-v4-flash");
    }

    [Fact]
    public async Task ExecuteAsync_converts_two_segment_to_omniroute_selector_when_provider_is_omniroute()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("ok")
        };
        var provider = CreateProvider(processManager: processManager);

        await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                Provider = "omniroute",
                Model = "ds/deepseek-v4-flash",
                NoSession = true
            },
            "hello");

        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.Arguments.ShouldNotContain("--provider");
        processManager.LastContext.Arguments.ShouldContain("--model");
        processManager.LastContext.Arguments.ShouldContain("omniroute/ds/deepseek-v4-flash");
    }

    [Theory]
    [InlineData("omniroute/ds/deepseek-v4-flash", "omniroute/ds/deepseek-v4-flash")]
    [InlineData("OmniRoute/glm/glm-4.7", "OmniRoute/glm/glm-4.7")]
    [InlineData("ds/deepseek-v4-flash", "ds/deepseek-v4-flash")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("omniroute/", "omniroute/")]
    [InlineData("omniroute/omniroute/x/y", "omniroute/omniroute/x/y")]
    public void NormalizeModelName_produces_expected_result(string? input, string expected)
    {
        OmpProvider.NormalizeModelName(input).ShouldBe(expected);
    }

    [Fact]
    public async Task ExecuteAsync_rejects_invalid_thinking_level_without_starting_a_process()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline")
        };
        var provider = CreateProvider(processManager: processManager);

        var act = async () => await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                Thinking = "balanced"
            },
            "Plan a trip.");

        var exception = await Should.ThrowAsync<ArgumentException>(act);
        exception.Message.ShouldContain("Invalid Pi thinking level 'balanced'");
        exception.Message.ShouldContain("medium");
        processManager.LastContext.ShouldBeNull();
    }

    [Theory]
    [InlineData("off")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("HIGH")]
    public async Task ExecuteAsync_accepts_documented_thinking_levels(string thinking)
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline")
        };
        var provider = CreateProvider(processManager: processManager);

        await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                Thinking = thinking,
                NoSession = true
            },
            "Plan a trip.");

        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.Arguments.ShouldContain("--thinking");
        var normalizedThinking = processManager.LastContext.Arguments
            .SkipWhile(arg => arg != "--thinking")
            .Skip(1)
            .FirstOrDefault();
        normalizedThinking.ShouldBe(thinking, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_normalizes_pi_json_events_into_shared_cli_messages()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                DisableAllTools = true,
                NoSession = true
            },
            "Plan a trip.");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "terminal.completed"]);
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-1");
        messages[1].Content.GetProperty("text").GetString().ShouldBe("Trip outline");
        messages[1].Content.GetProperty("provider").GetString().ShouldBe("omniroute");
        messages[1].Content.GetProperty("timestamp").GetInt64().ShouldBe(1780750996887L);
        messages[1].Content.GetProperty("usage").GetProperty("input").GetInt32().ShouldBe(12);
        messages[1].Content.GetProperty("usage").GetProperty("output").GetInt32().ShouldBe(3);
        messages[2].Content.TryGetProperty("text", out _).ShouldBeFalse();
        messages[2].Content.GetProperty("stop_reason").GetString().ShouldBe("stop");
        messages[2].Content.GetProperty("timestamp").GetInt64().ShouldBe(1780750996887L);
        messages[2].Content.GetProperty("usage").GetProperty("totalTokens").GetInt32().ShouldBe(15);
    }

    [Fact]
    public async Task ExecuteAsync_streams_message_updates_before_process_exit()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateStreamingExecutionResult(),
            OutputLineDelayMilliseconds = 450
        };
        var provider = CreateProvider(processManager: processManager);

        await using var enumerator = provider.ExecuteAsync(
                new PiOptions
                {
                    WorkingDirectory = "/tmp/pi-project",
                    Model = "glm/glm-4.7",
                    NoSession = true
                },
                "Reply with hello.")
            .GetAsyncEnumerator();

        var stopwatch = Stopwatch.StartNew();
        (await enumerator.MoveNextAsync()).ShouldBeTrue();
        stopwatch.Stop();

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1.5));
        enumerator.Current.Type.ShouldBe("session.started");

        var messages = new List<CliMessage> { enumerator.Current };
        while (await enumerator.MoveNextAsync())
        {
            messages.Add(enumerator.Current);
        }

        messages.ShouldContain(static message => message.Type == "assistant.thought");
        messages.Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .ShouldContain("hello");
        messages.ShouldContain(static message => message.Type == "terminal.completed");
    }

    [Fact]
    public async Task ExecuteAsync_converts_cumulative_text_snapshots_into_incremental_assistant_messages()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateCumulativeStreamingExecutionResult()
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                NoSession = true
            },
            "Reply with digits.");

        messages.Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .ShouldBe(["1", "2", "3"]);
        var cumulativeTerminalMessage = messages.Single(static message => message.Type == "terminal.completed");
        cumulativeTerminalMessage.Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_preserves_whitespace_only_assistant_text_deltas()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateWhitespaceStreamingExecutionResult()
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                NoSession = true
            },
            "Preserve whitespace.");

        var assistantTexts = messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .ToArray();

        assistantTexts.ShouldBe(["## Heading", "\n\n", "- [x] 1.1 item", " ", "tail"]);
        string.Concat(assistantTexts).ShouldBe("## Heading\n\n- [x] 1.1 item tail");
    }

    [Fact]
    public async Task ExecuteAsync_preserves_golden_markdown_structure_at_mapper_boundary()
    {
        const string golden =
            "## H2\n\n### H3\n\n- [x] 1.1–1.4\n- item\n\n| col | val |\n| --- | --- |\n| a | b |\n\n**bold** 中文Latin\n\nSummary done.\n\nSystem follow-up";

        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult(golden)
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                NoSession = true
            },
            "Golden markdown.");

        var assistantText = messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .Single();

        assistantText.ShouldBe(golden);
        assistantText.ShouldContain("- [x] 1.1–1.4");
        assistantText.ShouldContain("| col | val |\n| --- | --- |\n| a | b |");
        assistantText.ShouldContain("**bold** 中文Latin");
        assistantText.ShouldContain("Summary done.\n\nSystem follow-up");
    }

    [Fact]
    public async Task ExecuteAsync_deduplicates_replayed_assistant_prefix_after_tool_turns()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateCrossTurnReplayExecutionResult()
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                NoSession = true
            },
            "Analyze and commit changes.");

        messages.Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .ShouldBe(["Alpha", "Beta"]);
        messages.ShouldContain(static message => message.Type == "tool.call");
        messages.ShouldContain(static message => message.Type == "tool.completed");
        var replayTerminalMessage = messages.Single(static message => message.Type == "terminal.completed");
        replayTerminalMessage.Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_normalizes_toolcall_updates_and_results_into_shared_cli_messages()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateToolCallExecutionResult()
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                NoSession = true
            },
            "List the current directory.");

        messages.Select(static message => message.Type).ShouldBe(
        [
            "session.started",
            "tool.call",
            "tool.completed",
            "assistant",
            "terminal.completed"
        ]);
        messages[1].Content.GetProperty("tool_call_id").GetString().ShouldBe("tool-1");
        messages[1].Content.GetProperty("tool_name").GetString().ShouldBe("ls");
        messages[2].Content.GetProperty("tool_call_id").GetString().ShouldBe("tool-1");
        messages[2].Content.GetProperty("text").GetString().ShouldContain("README.md");
        messages[2].Content.GetProperty("timestamp").GetInt64().ShouldBe(1780796361956L);
        messages[2].Content.GetProperty("update").GetProperty("rawOutput").ValueKind.ShouldBe(JsonValueKind.Array);
        messages[2].Content.GetProperty("update").GetProperty("rawOutput").EnumerateArray().First().GetProperty("text").GetString().ShouldContain("README.md");
        messages[4].Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_omits_terminal_completed_text_when_final_turn_replays_same_assistant_snapshot()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = "/tmp/pi-project",
                Model = "glm/glm-4.7",
                DisableAllTools = true,
                NoSession = true
            },
            "Plan a trip.");

        messages[^1].Type.ShouldBe("terminal.completed");
        messages[^1].Content.TryGetProperty("text", out _).ShouldBeFalse();
        messages[^1].Content.GetProperty("stop_reason").GetString().ShouldBe("stop");
    }

    [Fact]
    public async Task ExecuteAsync_marks_matching_requested_session_id_as_resumed()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline", "pi-session-1")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                SessionId = "pi-session-1",
                Model = "glm/glm-4.7"
            },
            "Plan a trip.");

        messages[0].Type.ShouldBe("session.resumed");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("pi-session-1");
        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("pi-session-1");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
        messages[0].Content.GetProperty("resumed").GetBoolean().ShouldBeTrue();
        messages[0].Content.GetProperty("restarted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_marks_mismatched_requested_session_id_as_restarted()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateSuccessfulExecutionResult("Trip outline", "session-new")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                SessionId = "pi-session-1",
                Model = "glm/glm-4.7"
            },
            "Plan a trip.");

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("session_id").GetString().ShouldBe("session-new");
        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("pi-session-1");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("restarted");
        messages[0].Content.GetProperty("resumed").GetBoolean().ShouldBeFalse();
        messages[0].Content.GetProperty("restarted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_surfaces_non_zero_exit_as_terminal_failure_with_diagnostics()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = CreateFailedExecutionResult("model rejected", "provider stderr")
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(provider, new PiOptions { Model = "glm/glm-4.7" }, "Plan a trip.");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        var failureText = messages[^1].Content.GetProperty("text").GetString();
        failureText.ShouldNotBeNullOrWhiteSpace();
        failureText.ShouldContain("model rejected");
        failureText.ShouldContain("provider stderr");
        messages[^1].Content.GetProperty("exit_code").GetInt32().ShouldBe(1);
        messages[^1].Content.GetProperty("invalid_output_lines").EnumerateArray().Select(static value => value.GetString()).ShouldContain("Warning: Model alias fallback");
    }

    [Fact]
    public async Task ExecuteAsync_reports_invalid_json_output_as_terminal_failure()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = new ProcessResult(0, "plain text preamble\nthis is not json\n", string.Empty)
        };
        var provider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(provider, new PiOptions { NoSession = true }, "Plan a trip.");

        messages.ShouldHaveSingleItem();
        messages[0].Type.ShouldBe("terminal.failed");
        var failureText = messages[0].Content.GetProperty("text").GetString();
        failureText.ShouldNotBeNullOrWhiteSpace();
        failureText.ShouldContain("non-JSON output");
        messages[0].Content.GetProperty("invalid_output_lines").EnumerateArray().Select(static value => value.GetString()).ShouldContain("plain text preamble");
    }

    [Fact]
    public async Task PingAsync_reports_version_when_process_succeeds()
    {
        var processManager = new StubCliProcessManager
        {
            ExecuteResult = new ProcessResult(0, "0.78.1", string.Empty)
        };
        var provider = CreateProvider(processManager: processManager);

        var result = await provider.PingAsync();

        result.ProviderName.ShouldBe("pi");
        result.Success.ShouldBeTrue();
        result.Version.ShouldBe("0.78.1");
        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.Arguments.ShouldBe(["--version"]);
    }

    [Fact]
    public async Task PingAsync_returns_failure_when_executable_is_missing()
    {
        var provider = CreateProvider(executableResolver: new MissingExecutableResolver());

        var result = await provider.PingAsync();

        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ExecuteAsync_real_cli_trip_prompt_returns_assistant_output_or_actionable_failure_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        var resolver = new CliExecutableResolver();
        var executablePath = resolver.ResolveFirstAvailablePath(PiExecutableCandidates);
        if (executablePath is null)
        {
            throw new InvalidOperationException("Pi CLI was not found on PATH even though the real CLI lane was enabled.");
        }

        using var workspace = new TemporaryPiWorkspace();
        await using var provider = new PiProvider(resolver, new CliProcessManager());
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var messages = await CollectMessagesAsync(
            provider,
            new PiOptions
            {
                WorkingDirectory = workspace.WorkingDirectory,
                Provider = "omniroute",
                Model = "glm/glm-4.7",
                DisableAllTools = true,
                NoSession = true
            },
            "Plan a two-day Chongqing trip and reply with exactly three short bullet points.",
            cancellationTokenSource.Token);

        var assistantTexts = messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToArray();

        if (assistantTexts.Length > 0)
        {
            assistantTexts[0].ShouldNotBeNullOrWhiteSpace();
            messages.ShouldContain(static message => message.Type == "terminal.completed");
            return;
        }

        var failureMessage = string.Join(
            Environment.NewLine,
            messages
                .SelectMany(static message => EnumerateStringValues(message.Content))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.Ordinal));

        failureMessage.ShouldNotBeNullOrWhiteSpace();
        RealCliInvocationTestHarness.AssertActionableFailure("pi", failureMessage);
    }

    private static PiProvider CreateProvider(
        CliExecutableResolver? executableResolver = null,
        CliProcessManager? processManager = null,
        IRuntimeEnvironmentResolver? runtimeEnvironmentResolver = null)
    {
        return new PiProvider(
            executableResolver ?? new StubExecutableResolver(),
            processManager ?? new StubCliProcessManager(),
            runtimeEnvironmentResolver ?? new StubRuntimeEnvironmentResolver());
    }

    private static async Task<List<CliMessage>> CollectMessagesAsync(
        ICliProvider<PiOptions> provider,
        PiOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static ProcessResult CreateSuccessfulExecutionResult(string assistantText, string sessionId = "session-1")
    {
        var assistantMessage = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "thinking", thinking = "internal" },
                new { type = "text", text = assistantText }
            },
            api = "openai-completions",
            provider = "omniroute",
            model = "glm/glm-4.7",
            usage = new
            {
                input = 12,
                output = 3,
                cacheRead = 0,
                cacheWrite = 0,
                totalTokens = 15,
                cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 }
            },
            stopReason = "stop",
            timestamp = 1780750996887L,
            responseId = "response-1",
            responseModel = "glm-4.7"
        };

        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = sessionId, timestamp = "2026-06-06T13:03:16.799Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new { type = "message_end", message = assistantMessage }),
            JsonSerializer.Serialize(new { type = "turn_end", message = assistantMessage, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new
            {
                type = "agent_end",
                messages = new object[]
                {
                    new { role = "user", content = new object[] { new { type = "text", text = "Plan a trip." } }, timestamp = 1780750996867L },
                    assistantMessage
                },
                willRetry = false
            })
        };

        return new ProcessResult(0, string.Join(Environment.NewLine, lines) + Environment.NewLine, string.Empty);
    }

    private static ProcessResult CreateFailedExecutionResult(string errorMessage, string standardError)
    {
        var assistantMessage = new
        {
            role = "assistant",
            content = Array.Empty<object>(),
            api = "openai-completions",
            provider = "omniroute",
            model = "glm/glm-4.7",
            usage = new
            {
                input = 0,
                output = 0,
                cacheRead = 0,
                cacheWrite = 0,
                totalTokens = 0,
                cost = new { input = 0, output = 0, cacheRead = 0, cacheWrite = 0, total = 0 }
            },
            stopReason = "error",
            timestamp = 1780751012306L,
            errorMessage
        };

        var lines = new[]
        {
            "Warning: Model alias fallback",
            JsonSerializer.Serialize(new { type = "session", version = 3, id = "session-1", timestamp = "2026-06-06T13:03:32.219Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new { type = "message_end", message = assistantMessage }),
            JsonSerializer.Serialize(new { type = "turn_end", message = assistantMessage, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { type = "agent_end", messages = new object[] { assistantMessage }, willRetry = false })
        };

        return new ProcessResult(1, string.Join(Environment.NewLine, lines) + Environment.NewLine, standardError);
    }

    private static ProcessResult CreateStreamingExecutionResult(string sessionId = "session-stream")
    {
        var thinkingPartial = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "thinking", thinking = "Plan" }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-stream-1",
            responseModel = "glm-4.7"
        };

        var textPartial = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "thinking", thinking = "Plan" },
                new { type = "text", text = "hello" }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-stream-1",
            responseModel = "glm-4.7"
        };

        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = sessionId, timestamp = "2026-06-07T09:22:21.958Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "thinking_delta",
                    partial = thinkingPartial
                },
                message = thinkingPartial
            }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "text_delta",
                    partial = textPartial
                },
                message = textPartial
            }),
            JsonSerializer.Serialize(new { type = "message_end", message = textPartial }),
            JsonSerializer.Serialize(new { type = "turn_end", message = textPartial, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { type = "agent_end", messages = new object[] { textPartial }, willRetry = false })
        };

        return new ProcessResult(0, string.Join(Environment.NewLine, lines) + Environment.NewLine, string.Empty);
    }

    private static ProcessResult CreateWhitespaceStreamingExecutionResult(string sessionId = "session-whitespace")
    {
        object Partial(string text) => new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "text", text }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-whitespace-1",
            responseModel = "glm-4.7"
        };

        var snapshots = new[]
        {
            "## Heading",
            "## Heading\n\n",
            "## Heading\n\n- [x] 1.1 item",
            "## Heading\n\n- [x] 1.1 item ",
            "## Heading\n\n- [x] 1.1 item tail"
        };

        var lines = new List<string>
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = sessionId, timestamp = "2026-06-07T12:00:00.000Z", cwd = "/tmp/pi-project" })
        };

        foreach (var snapshot in snapshots)
        {
            var partial = Partial(snapshot);
            lines.Add(JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "text_delta",
                    partial
                },
                message = partial
            }));
        }

        var finalPartial = Partial(snapshots[^1]);
        lines.Add(JsonSerializer.Serialize(new { type = "message_end", message = finalPartial }));
        lines.Add(JsonSerializer.Serialize(new { type = "turn_end", message = finalPartial, toolResults = Array.Empty<object>() }));
        lines.Add(JsonSerializer.Serialize(new { type = "agent_end", messages = new object[] { finalPartial }, willRetry = false }));

        return new ProcessResult(0, string.Join(Environment.NewLine, lines) + Environment.NewLine, string.Empty);
    }

    private static ProcessResult CreateCumulativeStreamingExecutionResult(string sessionId = "session-cumulative")
    {
        var textPartial1 = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "text", text = "1" }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-cumulative-1",
            responseModel = "glm-4.7"
        };

        var textPartial2 = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "text", text = "12" }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-cumulative-1",
            responseModel = "glm-4.7"
        };

        var textPartial3 = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "text", text = "123" }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-cumulative-1",
            responseModel = "glm-4.7"
        };

        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = sessionId, timestamp = "2026-06-07T12:00:00.000Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "text_delta",
                    partial = textPartial1
                },
                message = textPartial1
            }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "text_delta",
                    partial = textPartial2
                },
                message = textPartial2
            }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "text_end",
                    partial = textPartial3
                },
                message = textPartial3
            }),
            JsonSerializer.Serialize(new { type = "message_end", message = textPartial3 }),
            JsonSerializer.Serialize(new { type = "turn_end", message = textPartial3, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { type = "agent_end", messages = new object[] { textPartial3 }, willRetry = false })
        };

        return new ProcessResult(0, string.Join(Environment.NewLine, lines) + Environment.NewLine, string.Empty);
    }

    private static ProcessResult CreateToolCallExecutionResult(string sessionId = "session-tool")
    {
        var toolUseAssistant = new
        {
            role = "assistant",
            content = new object[]
            {
                new
                {
                    type = "thinking",
                    thinking = "我已经有足够信息来分析项目，接下来再确认几个细节。"
                },
                new
                {
                    type = "toolCall",
                    id = "tool-1",
                    name = "ls",
                    arguments = new
                    {
                        path = "/tmp/pi-project"
                    },
                    partialArgs = "{\"path\":\"/tmp/pi-project\"}",
                    streamIndex = 0
                }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "toolUse",
            responseId = "response-tool-1",
            responseModel = "glm-4.7"
        };

        var finalAssistant = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "text", text = "Listed the directory." }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-tool-2",
            responseModel = "glm-4.7"
        };

        var toolResult = new
        {
            role = "toolResult",
            toolCallId = "tool-1",
            toolName = "ls",
            content = new object[]
            {
                new { type = "text", text = "README.md\nrepos/\nscripts/" }
            },
            isError = false,
            timestamp = 1780796361956L
        };

        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = sessionId, timestamp = "2026-06-07T09:39:20.000Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "toolcall_start",
                    contentIndex = 0,
                    partial = toolUseAssistant
                },
                message = toolUseAssistant
            }),
            JsonSerializer.Serialize(new { type = "message_end", message = toolUseAssistant }),
            JsonSerializer.Serialize(new { type = "message_end", message = toolResult }),
            JsonSerializer.Serialize(new { type = "turn_end", message = toolUseAssistant, toolResults = new object[] { toolResult } }),
            JsonSerializer.Serialize(new { type = "message_end", message = finalAssistant }),
            JsonSerializer.Serialize(new { type = "turn_end", message = finalAssistant, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { type = "agent_end", messages = new object[] { toolUseAssistant, toolResult, finalAssistant }, willRetry = false })
        };

        return new ProcessResult(0, string.Join(Environment.NewLine, lines) + Environment.NewLine, string.Empty);
    }

    private static ProcessResult CreateCrossTurnReplayExecutionResult(string sessionId = "session-replay")
    {
        var toolUseAssistant = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "text", text = "Alpha" },
                new
                {
                    type = "toolCall",
                    id = "tool-1",
                    name = "ls",
                    arguments = new
                    {
                        path = "/tmp/pi-project"
                    },
                    partialArgs = "{\"path\":\"/tmp/pi-project\"}",
                    streamIndex = 0
                }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "toolUse",
            responseId = "response-replay-1",
            responseModel = "glm-4.7"
        };

        var finalAssistant = new
        {
            role = "assistant",
            content = new object[]
            {
                new { type = "text", text = "AlphaBeta" }
            },
            provider = "omniroute",
            model = "glm/glm-4.7",
            stopReason = "stop",
            responseId = "response-replay-2",
            responseModel = "glm-4.7"
        };

        var toolResult = new
        {
            role = "toolResult",
            toolCallId = "tool-1",
            toolName = "ls",
            content = new object[]
            {
                new { type = "text", text = "README.md" }
            },
            isError = false,
            timestamp = 1780796361956L
        };

        var lines = new[]
        {
            JsonSerializer.Serialize(new { type = "session", version = 3, id = sessionId, timestamp = "2026-06-07T13:00:00.000Z", cwd = "/tmp/pi-project" }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "toolcall_start",
                    partial = toolUseAssistant
                },
                message = toolUseAssistant
            }),
            JsonSerializer.Serialize(new { type = "message_end", message = toolUseAssistant }),
            JsonSerializer.Serialize(new { type = "message_end", message = toolResult }),
            JsonSerializer.Serialize(new { type = "turn_end", message = toolUseAssistant, toolResults = new object[] { toolResult } }),
            JsonSerializer.Serialize(new { type = "turn_start" }),
            JsonSerializer.Serialize(new
            {
                type = "message_update",
                assistantMessageEvent = new
                {
                    type = "text_delta",
                    partial = finalAssistant
                },
                message = finalAssistant
            }),
            JsonSerializer.Serialize(new { type = "message_end", message = finalAssistant }),
            JsonSerializer.Serialize(new { type = "turn_end", message = finalAssistant, toolResults = Array.Empty<object>() }),
            JsonSerializer.Serialize(new { type = "agent_end", messages = new object[] { toolUseAssistant, toolResult, finalAssistant }, willRetry = false })
        };

        return new ProcessResult(0, string.Join(Environment.NewLine, lines) + Environment.NewLine, string.Empty);
    }

    private static IEnumerable<string> EnumerateStringValues(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value;
                }

                yield break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var propertyValue in EnumerateStringValues(property.Value))
                    {
                        yield return propertyValue;
                    }
                }

                yield break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var itemValue in EnumerateStringValues(item))
                    {
                        yield return itemValue;
                    }
                }

                yield break;
            default:
                yield break;
        }
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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

    private sealed class StubCliProcessManager : CliProcessManager
    {
        public ProcessStartContext? LastContext { get; private set; }
        public ProcessResult ExecuteResult { get; init; } = new(0, string.Empty, string.Empty);
        public int OutputLineDelayMilliseconds { get; init; }

        public override Task<ProcessResult> ExecuteAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return Task.FromResult(ExecuteResult);
        }

        public override async ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            var launchContext = await CreateLaunchContextAsync(context, cancellationToken);
            return await base.StartAsync(launchContext, cancellationToken);
        }

        private async Task<ProcessStartContext> CreateLaunchContextAsync(ProcessStartContext originalContext, CancellationToken cancellationToken)
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), $"hagicode-libs-pi-stub-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var launchWorkingDirectory = !string.IsNullOrWhiteSpace(originalContext.WorkingDirectory)
                                         && Directory.Exists(originalContext.WorkingDirectory)
                ? originalContext.WorkingDirectory
                : tempDirectory;

            var stdoutPath = Path.Combine(tempDirectory, "stdout.txt");
            var stderrPath = Path.Combine(tempDirectory, "stderr.txt");
            await File.WriteAllTextAsync(stdoutPath, ExecuteResult.StandardOutput ?? string.Empty, cancellationToken);
            await File.WriteAllTextAsync(stderrPath, ExecuteResult.StandardError ?? string.Empty, cancellationToken);

            if (OperatingSystem.IsWindows())
            {
                var scriptPath = Path.Combine(tempDirectory, "run.ps1");
                var script = $@"
$stdoutPath = '{EscapePowerShellLiteral(stdoutPath)}'
$stderrPath = '{EscapePowerShellLiteral(stderrPath)}'
$delayMs = {OutputLineDelayMilliseconds}
Get-Content -LiteralPath $stdoutPath | ForEach-Object {{ Write-Output $_; if ($delayMs -gt 0) {{ Start-Sleep -Milliseconds $delayMs }} }}
Get-Content -LiteralPath $stderrPath | ForEach-Object {{ [Console]::Error.WriteLine($_); if ($delayMs -gt 0) {{ Start-Sleep -Milliseconds $delayMs }} }}
exit {ExecuteResult.ExitCode}
";
                await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
                return new ProcessStartContext
                {
                    ExecutablePath = "powershell",
                    Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath],
                    WorkingDirectory = launchWorkingDirectory,
                    OutputEncoding = originalContext.OutputEncoding,
                    InputEncoding = originalContext.InputEncoding
                };
            }

            var scriptPathUnix = Path.Combine(tempDirectory, "run.sh");
            var delaySeconds = (OutputLineDelayMilliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            var scriptUnix = $"#!/bin/sh\nstdout_path='{EscapeShellLiteral(stdoutPath)}'\nstderr_path='{EscapeShellLiteral(stderrPath)}'\ndelay_seconds='{delaySeconds}'\nwhile IFS= read -r line || [ -n \"$line\" ]; do\n  printf '%s\\n' \"$line\"\n  if [ \"$delay_seconds\" != '0' ]; then sleep \"$delay_seconds\"; fi\ndone < \"$stdout_path\"\nwhile IFS= read -r line || [ -n \"$line\" ]; do\n  printf '%s\\n' \"$line\" >&2\n  if [ \"$delay_seconds\" != '0' ]; then sleep \"$delay_seconds\"; fi\ndone < \"$stderr_path\"\nexit {ExecuteResult.ExitCode}\n";
            await File.WriteAllTextAsync(scriptPathUnix, scriptUnix, cancellationToken);
            File.SetUnixFileMode(scriptPathUnix, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            return new ProcessStartContext
            {
                ExecutablePath = scriptPathUnix,
                Arguments = [],
                WorkingDirectory = launchWorkingDirectory,
                OutputEncoding = originalContext.OutputEncoding,
                InputEncoding = originalContext.InputEncoding
            };
        }

        private static string EscapeShellLiteral(string value)
        {
            return value.Replace("'", "'\"'\"'");
        }

        private static string EscapePowerShellLiteral(string value)
        {
            return value.Replace("'", "''");
        }
    }

    private sealed class TemporaryPiWorkspace : IDisposable
    {
        private bool _disposed;

        public TemporaryPiWorkspace()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), $"hagicode-libs-pi-{Guid.NewGuid():N}");
            WorkingDirectory = Path.Combine(RootDirectory, "workspace");

            Directory.CreateDirectory(WorkingDirectory);
            File.WriteAllText(Path.Combine(WorkingDirectory, "README.md"), "# pi real cli workspace");
        }

        public string RootDirectory { get; }

        public string WorkingDirectory { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, recursive: true);
            }
        }
    }
}
