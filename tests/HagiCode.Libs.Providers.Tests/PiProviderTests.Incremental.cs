using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Pi;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Pi 0.84+ incremental <c>message_update</c> format: the fixtures under <c>Fixtures/Pi</c> are trimmed captures from pi 1.2.0.
/// </summary>
public sealed partial class PiProviderTests
{
    private static readonly PiOptions FixtureOptions = new()
    {
        WorkingDirectory = "/tmp/pi-project",
        Model = "mock-model",
        NoSession = true
    };

    [Fact]
    public async Task ExecuteAsync_pi_1x_text_run_streams_marked_fragments_before_process_exit()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The gated fake process is a POSIX shell script.
        }

        // The fake process prints the first part of the capture (up to the second text fragment), then blocks until the test
        // creates the gate file, and only then prints the rest and exits. A provider that waited for the end of the message
        // or of the process would never deliver the first fragments, so the read below would time out.
        var lines = LoadFixture("text-turn").StandardOutput!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var secondFragmentIndex = Array.FindLastIndex(lines, static line => line.Contains("\"delta\":\"\\n\\n\""));
        secondFragmentIndex.ShouldBeGreaterThan(0);
        var directory = Path.Combine(Path.GetTempPath(), $"hagicode-libs-pi-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var gatePath = Path.Combine(directory, "gate");
        var processManager = new GatedProcessManager(
            string.Join('\n', lines.Take(secondFragmentIndex + 1)) + "\n",
            string.Join('\n', lines.Skip(secondFragmentIndex + 1)) + "\n",
            directory,
            gatePath);
        var provider = CreateProvider(processManager: processManager);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var messages = new List<CliMessage>();
            await foreach (var message in provider.ExecuteAsync(FixtureOptions, "Reply with markdown.", timeout.Token))
            {
                messages.Add(message);
                if (message.Type == "assistant" && AssistantTexts(messages).Count == 2)
                {
                    // Two fragments arrived while the process is still blocked on the gate.
                    File.Exists(gatePath).ShouldBeFalse();
                    File.WriteAllText(gatePath, "go");
                }
            }

            var fragments = AssistantTexts(messages);
            fragments.ShouldBe(["## Heading", "\n\n", "- [x] 1.1 item", " ", "tail"]);
            string.Concat(fragments).ShouldBe("## Heading\n\n- [x] 1.1 item tail");
            messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));

            var terminal = messages.Single(static message => message.Type == "terminal.completed");
            terminal.Content.TryGetProperty("text", out _).ShouldBeFalse();
            terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("stop");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_pi_1x_thinking_run_emits_thought_fragments_in_order()
    {
        var provider = CreateProvider(processManager: new StubCliProcessManager { ExecuteResult = LoadFixture("thinking-turn") });

        var messages = await CollectMessagesAsync(provider, FixtureOptions, "Think first.");

        var thoughts = messages.Where(static message => message.Type == "assistant.thought").ToArray();
        thoughts.Select(static message => message.Content.GetProperty("text").GetString())
            .ShouldBe(["Considering", " ", "the", " question."]);
        thoughts.ShouldAllBe(static message => IsDelta(message));

        // The repeated "The" is real content, not a replay.
        AssistantTexts(messages).ShouldBe(["The", " answer", ".", "\n", "The", " end."]);
        string.Concat(AssistantTexts(messages)).ShouldBe("The answer.\nThe end.");
        messages.Single(static message => message.Type == "terminal.completed").Content.TryGetProperty("text", out _).ShouldBeFalse();

        // thinking_end / text_end repeat what was streamed and must not add anything.
        messages.Count(static message => message.Type == "assistant.thought").ShouldBe(4);
    }

    [Fact]
    public async Task ExecuteAsync_pi_1x_tool_run_forwards_tool_input_once_and_keeps_thinking()
    {
        var provider = CreateProvider(processManager: new StubCliProcessManager { ExecuteResult = LoadFixture("tool-turn") });

        var messages = await CollectMessagesAsync(provider, FixtureOptions, "Run echo hello.");

        messages.Select(static message => message.Type).ShouldBe(
        [
            "session.started",
            "assistant.thought",
            "assistant.thought",
            "assistant",
            "tool.call",
            "tool.update",
            "tool.completed",
            "assistant",
            "assistant",
            "assistant",
            "terminal.completed"
        ]);

        var call = messages.Single(static message => message.Type == "tool.call");
        var update = messages.Single(static message => message.Type == "tool.update");
        var completed = messages.Single(static message => message.Type == "tool.completed");
        call.Content.GetProperty("tool_call_id").GetString().ShouldBe("call_mock_1");
        call.Content.GetProperty("tool_name").GetString().ShouldBe("bash");
        call.Content.GetProperty("status").GetString().ShouldBe("running");
        call.Content.GetProperty("update").TryGetProperty("rawInput", out _).ShouldBeFalse();

        update.Content.GetProperty("tool_call_id").GetString().ShouldBe("call_mock_1");
        update.Content.GetProperty("tool_name").GetString().ShouldBe("bash");
        var rawInput = update.Content.GetProperty("update").GetProperty("rawInput");
        rawInput.ValueKind.ShouldBe(JsonValueKind.Object);
        rawInput.GetProperty("command").GetString().ShouldBe("echo hello");

        completed.Content.GetProperty("tool_call_id").GetString().ShouldBe("call_mock_1");
        completed.Content.GetProperty("tool_name").GetString().ShouldBe("bash");
        var completedText = completed.Content.GetProperty("text").GetString();
        completedText.ShouldNotBeNull();
        completedText.ShouldContain("hello");

        // Thinking before the tool call is kept, and no partial argument JSON is forwarded as text or input.
        messages.Where(static message => message.Type == "assistant.thought")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .ShouldBe(["Need to", " run echo."]);
        AssistantTexts(messages).ShouldBe(["Running it now.", "The command printed", " hello", "."]);
        AssistantTexts(messages).ShouldNotContain(static text => text!.Contains("{\"command\""));
        messages.Where(static message => message.Type.StartsWith("tool.", StringComparison.Ordinal))
            .Select(static message => message.Content.GetProperty("update"))
            .Where(static update => update.TryGetProperty("rawInput", out _))
            .ShouldAllBe(static update => update.GetProperty("rawInput").ValueKind == JsonValueKind.Object);

        messages.Single(static message => message.Type == "terminal.completed").Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_pi_1x_error_run_produces_terminal_failed()
    {
        var provider = CreateProvider(processManager: new StubCliProcessManager { ExecuteResult = LoadFixture("error-turn") });

        var messages = await CollectMessagesAsync(provider, FixtureOptions, "Fail.");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        var errorText = messages[^1].Content.GetProperty("text").GetString();
        errorText.ShouldNotBeNull();
        errorText.ShouldContain("mock upstream rejected");
        messages[^1].Content.GetProperty("stop_reason").GetString().ShouldBe("error");
    }

    [Theory]
    [InlineData("omniroute", null)]
    [InlineData("omniroute", "")]
    [InlineData("omniroute", "   ")]
    [InlineData("anthropic", null)]
    [InlineData("anthropic", "  ")]
    public async Task ExecuteAsync_fails_before_launch_when_provider_has_no_model(string provider, string? model)
    {
        var processManager = new StubCliProcessManager { ExecuteResult = LoadFixture("text-turn") };
        var piProvider = CreateProvider(processManager: processManager);

        var messages = await CollectMessagesAsync(
            piProvider,
            new PiOptions { Provider = provider, Model = model, NoSession = true },
            "hello");

        processManager.LastContext.ShouldBeNull();
        var failure = messages.ShouldHaveSingleItem();
        failure.Type.ShouldBe("terminal.failed");
        var text = failure.Content.GetProperty("text").GetString();
        text.ShouldNotBeNull();
        text.ShouldContain(provider);
        text.ShouldContain("model is required when a provider is set");
    }

    [Fact]
    public async Task ExecuteAsync_keeps_model_without_provider_and_provider_with_model_unchanged()
    {
        var processManager = new StubCliProcessManager { ExecuteResult = LoadFixture("text-turn") };
        var provider = CreateProvider(processManager: processManager);

        await CollectMessagesAsync(provider, new PiOptions { Model = "claude-sonnet", NoSession = true }, "hello");
        processManager.LastContext.ShouldNotBeNull();
        processManager.LastContext.Arguments.ShouldNotContain("--provider");
        processManager.LastContext.Arguments.SkipWhile(static argument => argument != "--model").Skip(1).First().ShouldBe("claude-sonnet");

        await CollectMessagesAsync(provider, new PiOptions { Provider = "anthropic", Model = "claude-sonnet", NoSession = true }, "hello");
        processManager.LastContext.Arguments.SkipWhile(static argument => argument != "--provider").Skip(1).First().ShouldBe("anthropic");
        processManager.LastContext.Arguments.SkipWhile(static argument => argument != "--model").Skip(1).First().ShouldBe("claude-sonnet");
    }

    [Fact]
    public void Mapper_keeps_interleaved_blocks_separate_when_end_events_arrive_late()
    {
        var messages = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "text_start", contentIndex = 0 }),
            Update(new { type = "text_delta", contentIndex = 0, delta = "Let me " }),
            Update(new { type = "toolcall_start", contentIndex = 1, id = "call-1", toolName = "read" }),
            Update(new { type = "text_delta", contentIndex = 0, delta = "check." }),
            Update(new { type = "toolcall_delta", contentIndex = 1, delta = "{\"path\":" }),
            // The text block ends after the tool-call block started.
            Update(new { type = "text_end", contentIndex = 0, content = "Let me check." }),
            Update(new { type = "toolcall_delta", contentIndex = 1, delta = "\"a.txt\"}" }),
            Update(new { type = "toolcall_end", contentIndex = 1, toolCall = new { type = "toolCall", id = "call-1", name = "read", arguments = new { path = "a.txt" } } }),
            FinishLine(new object[]
            {
                new { type = "text", text = "Let me check." },
                new { type = "toolCall", id = "call-1", name = "read", arguments = new { path = "a.txt" } }
            }, "toolUse"));

        AssistantTexts(messages).ShouldBe(["Let me ", "check."]);
        messages.Select(static message => message.Type).ShouldBe(
            ["session.started", "assistant", "tool.call", "assistant", "tool.update", "terminal.completed"]);
        var update = messages.Single(static message => message.Type == "tool.update");
        update.Content.GetProperty("update").GetProperty("rawInput").GetProperty("path").GetString().ShouldBe("a.txt");
        AssistantTexts(messages).ShouldNotContain(static text => text!.Contains("a.txt"));
    }

    [Fact]
    public void Mapper_emits_only_the_missing_suffix_when_text_end_extends_the_streamed_text()
    {
        var messages = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "text_delta", contentIndex = 0, delta = "Hello" }),
            Update(new { type = "text_end", contentIndex = 0, content = "Hello world" }),
            FinishLine([new { type = "text", text = "Hello world" }], "stop"));

        AssistantTexts(messages).ShouldBe(["Hello", " world"]);
        messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));
        messages[^1].Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public void Mapper_emits_only_the_missing_suffix_when_message_end_extends_the_streamed_text()
    {
        var messages = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "text_delta", contentIndex = 0, delta = "Hel" }),
            Update(new { type = "text_delta", contentIndex = 0, delta = "lo" }),
            FinishLine([new { type = "text", text = "Hello world" }], "stop"));

        AssistantTexts(messages).ShouldBe(["Hel", "lo", " world"]);
        messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));
        messages[^1].Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public void Mapper_emits_nothing_extra_when_the_final_text_diverges_from_the_streamed_text()
    {
        var messages = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "text_delta", contentIndex = 0, delta = "Hello" }),
            Update(new { type = "text_end", contentIndex = 0, content = "Goodbye" }),
            FinishLine([new { type = "text", text = "Goodbye" }], "stop"));

        // Nothing is replayed; the terminal message carries the final text for the consumer to reconcile.
        AssistantTexts(messages).ShouldBe(["Hello"]);
        messages[^1].Type.ShouldBe("terminal.completed");
        messages[^1].Content.GetProperty("text").GetString().ShouldBe("Goodbye");
    }

    [Fact]
    public void Mapper_emits_a_message_that_never_streamed_once_at_its_end_in_the_incremental_format()
    {
        var messages = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "text_delta", contentIndex = 0, delta = "First." }),
            FinishLine([new { type = "text", text = "First." }], "toolUse"),
            // The second assistant message has no updates at all.
            AssistantStartLine(),
            FinishLine([new { type = "text", text = "Second." }], "stop"));

        AssistantTexts(messages).ShouldBe(["First.", "Second."]);
        messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));
    }

    [Fact]
    public void Mapper_forwards_tool_execution_arguments_only_when_they_were_not_seen_before()
    {
        var withoutEnd = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "toolcall_start", contentIndex = 0, id = "call-1", toolName = "bash" }),
            Update(new { type = "toolcall_delta", contentIndex = 0, delta = "{\"command\":\"ls\"}" }),
            Line(new { type = "tool_execution_start", toolCallId = "call-1", toolName = "bash", args = new { command = "ls" } }),
            Line(new { type = "tool_execution_start", toolCallId = "call-1", toolName = "bash", args = new { command = "ls" } }));

        withoutEnd.Select(static message => message.Type).Take(3).ShouldBe(["session.started", "tool.call", "tool.update"]);
        withoutEnd.Count(static message => message.Type == "tool.update").ShouldBe(1);
        withoutEnd.Single(static message => message.Type == "tool.update")
            .Content.GetProperty("update").GetProperty("rawInput").GetProperty("command").GetString().ShouldBe("ls");

        var withEnd = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "toolcall_start", contentIndex = 0, id = "call-1", toolName = "bash" }),
            Update(new { type = "toolcall_end", contentIndex = 0, toolCall = new { type = "toolCall", id = "call-1", name = "bash", arguments = new { command = "ls" } } }),
            Line(new { type = "tool_execution_start", toolCallId = "call-1", toolName = "bash", args = new { command = "ls" } }));

        withEnd.Count(static message => message.Type == "tool.update").ShouldBe(1);
    }

    [Fact]
    public void Mapper_announces_a_tool_call_whose_start_event_was_not_seen()
    {
        var messages = Map(
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "toolcall_end", contentIndex = 0, toolCall = new { type = "toolCall", id = "call-9", name = "grep", arguments = new { pattern = "x" } } }));

        var call = messages.Single(static message => message.Type == "tool.call");
        call.Content.GetProperty("tool_call_id").GetString().ShouldBe("call-9");
        call.Content.GetProperty("update").GetProperty("rawInput").GetProperty("pattern").GetString().ShouldBe("x");
    }

    public static TheoryData<string> GoldenMarkdownSamples() => new()
    {
        "## H2\n\n### H3\n\n- [x] 1.1–1.4\n- item\n\n| col | val |\n| --- | --- |\n| a | b |\n\n**bold** 中文Latin\n\nSummary done.\n\nSystem follow-up",
        "## Heading\n\n- [x] 1.1 item tail",
        "| Name | Status |\n| --- | --- |\n| alpha | **done** |\n| beta | todo |\n",
        "The cat.\nThe dog.\n",
        "**Owner:** Dana\n**Status:** open — see `README.md`",
    };

    [Theory]
    [MemberData(nameof(GoldenMarkdownSamples))]
    public void Mapper_reassembles_golden_markdown_samples_from_token_and_whitespace_fragments(string source)
    {
        foreach (var fragments in new[]
                 {
                     Regex.Matches(source, @"\s+|\w+|[^\w\s]").Select(static match => match.Value).ToArray(),
                     source.Select(static character => character.ToString()).ToArray()
                 })
        {
            var lines = new List<string> { SessionLine("s-1", DateTimeOffset.UtcNow), AssistantStartLine() };
            lines.AddRange(fragments.Select(static fragment => Update(new { type = "text_delta", contentIndex = 0, delta = fragment })));
            lines.Add(Update(new { type = "text_end", contentIndex = 0, content = source }));
            lines.Add(FinishLine([new { type = "text", text = source }], "stop"));

            var messages = Map(lines.ToArray());

            var assistantTexts = AssistantTexts(messages);
            assistantTexts.Count.ShouldBe(fragments.Length);
            string.Concat(assistantTexts).ShouldBe(source);
            messages[^1].Type.ShouldBe("terminal.completed");
            messages[^1].Content.TryGetProperty("text", out _).ShouldBeFalse();
        }
    }

    public static TheoryData<string, string> TableFixtureSplits()
    {
        var data = new TheoryData<string, string>();
        foreach (var fixtureId in TableFixtureIds)
        {
            foreach (var strategy in TableSplitStrategies)
            {
                data.Add(fixtureId, strategy);
            }

            if (fixtureId == "T6")
            {
                data.Add(fixtureId, "S4");
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TableFixtureSplits))]
    public void Mapper_emits_each_table_fragment_as_a_delta_and_reassembles_the_source(string fixtureId, string strategy)
    {
        var source = TableFixtures[fixtureId];
        var fragments = SplitTable(source, strategy);

        var messages = Map(TextStreamLines(fragments).ToArray());

        AssistantTexts(messages).ShouldBe(fragments);
        messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));
        string.Concat(AssistantTexts(messages)).ShouldBe(source);
    }

    [Theory]
    [MemberData(nameof(TableFixtureSplits))]
    public void Mapper_adds_no_text_after_text_end_and_message_end_for_a_streamed_table(string fixtureId, string strategy)
    {
        var source = TableFixtures[fixtureId];
        var fragments = SplitTable(source, strategy);

        var messages = MapStreamedText(source, fragments);

        AssistantTexts(messages).ShouldBe(fragments);
        messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));
        messages.Single(static message => message.Type == "terminal.completed").Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public void Mapper_keeps_a_crlf_pair_as_one_fragment_of_a_streamed_table()
    {
        var source = TableFixtures["T6"];
        var fragments = new[] { "| Name | Status |", "\r\n", "| --- | --- |", "\r\n", "| alpha | done |", "\r\n" };

        var messages = MapStreamedText(source, fragments);

        AssistantTexts(messages).ShouldBe(fragments);
        string.Concat(AssistantTexts(messages)).ShouldBe(source);
    }

    [Fact]
    public void Mapper_keeps_cr_and_lf_in_separate_fragments_of_a_streamed_table()
    {
        var source = TableFixtures["T6"];
        var fragments = SplitTable(source, "S4");

        var messages = MapStreamedText(source, fragments);

        AssistantTexts(messages).ShouldBe(fragments);
        var carriageReturn = Array.IndexOf(fragments, "\r");
        carriageReturn.ShouldBeGreaterThan(-1);
        fragments[carriageReturn + 1].ShouldBe("\n");
        string.Concat(AssistantTexts(messages)).ShouldBe(source);
        messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));
    }

    [Fact]
    public void Mapper_streams_a_table_in_a_second_content_block_after_a_tool_call_without_loss()
    {
        var table = TableFixtures["T1"];
        var fragments = SplitTable(table, "S1");
        var toolCall = new { type = "toolCall", id = "call-1", name = "bash", arguments = new { command = "ls" } };
        var lines = new List<string>
        {
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "text_delta", contentIndex = 0, delta = "Running it now." }),
            Update(new { type = "text_end", contentIndex = 0, content = "Running it now." }),
            Update(new { type = "toolcall_start", contentIndex = 1, id = "call-1", toolName = "bash" }),
            Update(new { type = "toolcall_end", contentIndex = 1, toolCall })
        };
        lines.AddRange(fragments.Select(fragment => Update(new { type = "text_delta", contentIndex = 2, delta = fragment })));
        lines.Add(Update(new { type = "text_end", contentIndex = 2, content = table }));
        lines.Add(FinishLine([new { type = "text", text = "Running it now." }, toolCall, new { type = "text", text = table }], "stop"));

        var messages = Map(lines.ToArray());

        var texts = AssistantTexts(messages);
        texts[0].ShouldBe("Running it now.");
        texts.Skip(1).ShouldBe(fragments);
        string.Concat(texts).ShouldBe("Running it now." + table);
        messages.Where(static message => message.Type == "assistant").ShouldAllBe(static message => IsDelta(message));
        messages.Count(static message => message.Type == "tool.call").ShouldBe(1);
        messages.Single(static message => message.Type == "terminal.completed").Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public void Mapper_streams_a_table_in_a_later_message_after_a_tool_turn_without_mixing_blocks()
    {
        var table = TableFixtures["T1"];
        var fragments = SplitTable(table, "S3");
        var lines = new List<string>
        {
            SessionLine("s-1", DateTimeOffset.UtcNow),
            AssistantStartLine(),
            Update(new { type = "text_delta", contentIndex = 0, delta = "Running it now." }),
            Update(new { type = "text_end", contentIndex = 0, content = "Running it now." }),
            FinishLine([new { type = "text", text = "Running it now." }], "toolUse"),
            AssistantStartLine()
        };
        lines.AddRange(fragments.Select(fragment => Update(new { type = "text_delta", contentIndex = 0, delta = fragment })));
        lines.Add(Update(new { type = "text_end", contentIndex = 0, content = table }));
        lines.Add(FinishLine([new { type = "text", text = table }], "stop"));

        var messages = Map(lines.ToArray());

        var texts = AssistantTexts(messages);
        texts[0].ShouldBe("Running it now.");
        texts.Skip(1).ShouldBe(fragments);
        string.Concat(texts.Skip(1)).ShouldBe(table);
        messages.Single(static message => message.Type == "terminal.completed").Content.TryGetProperty("text", out _).ShouldBeFalse();
    }

    [Fact]
    public void Mapper_reports_a_session_created_by_this_run_as_started_even_for_a_requested_id()
    {
        var processStartedAt = new DateTimeOffset(2026, 10, 13, 6, 41, 26, TimeSpan.Zero);

        var messages = MapAs("pi-session-1", processStartedAt, SessionLine("pi-session-1", processStartedAt.AddMilliseconds(180)));

        var session = messages[0];
        session.Type.ShouldBe("session.started");
        session.Content.GetProperty("session_id").GetString().ShouldBe("pi-session-1");
        session.Content.GetProperty("requested_session_id").GetString().ShouldBe("pi-session-1");
        session.Content.GetProperty("resumeMode").GetString().ShouldBe("started");
        session.Content.GetProperty("resumed").GetBoolean().ShouldBeFalse();
        session.Content.GetProperty("restarted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Mapper_tolerates_a_header_slightly_before_the_process_start()
    {
        var processStartedAt = new DateTimeOffset(2026, 10, 13, 6, 41, 26, TimeSpan.Zero);

        var messages = MapAs("pi-session-1", processStartedAt, SessionLine("pi-session-1", processStartedAt.AddSeconds(-1.5)));

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("started");
    }

    [Fact]
    public void Mapper_reports_a_session_with_an_older_header_as_resumed()
    {
        var processStartedAt = new DateTimeOffset(2026, 10, 13, 6, 41, 26, TimeSpan.Zero);

        var messages = MapAs("pi-session-1", processStartedAt, SessionLine("pi-session-1", processStartedAt.AddSeconds(-3)));

        var session = messages[0];
        session.Type.ShouldBe("session.resumed");
        session.Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
        session.Content.GetProperty("resumed").GetBoolean().ShouldBeTrue();
        session.Content.GetProperty("restarted").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public void Mapper_reports_a_different_returned_id_as_restarted_regardless_of_the_header_time()
    {
        var processStartedAt = new DateTimeOffset(2026, 10, 13, 6, 41, 26, TimeSpan.Zero);

        var messages = MapAs("pi-session-1", processStartedAt, SessionLine("session-new", processStartedAt.AddMilliseconds(50)));

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("restarted");
        messages[0].Content.GetProperty("restarted").GetBoolean().ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a timestamp")]
    public void Mapper_falls_back_to_id_equality_when_the_header_time_is_missing_or_unreadable(string? timestamp)
    {
        var processStartedAt = new DateTimeOffset(2026, 10, 13, 6, 41, 26, TimeSpan.Zero);
        var line = timestamp is null
            ? Line(new { type = "session", version = 3, id = "pi-session-1", cwd = "/tmp/pi-project" })
            : Line(new { type = "session", version = 3, id = "pi-session-1", timestamp, cwd = "/tmp/pi-project" });

        var messages = MapAs("pi-session-1", processStartedAt, line);

        messages[0].Type.ShouldBe("session.resumed");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
    }

    [Fact]
    public void Mapper_falls_back_to_id_equality_when_no_process_start_time_is_known()
    {
        var messages = MapAs("pi-session-1", null, SessionLine("pi-session-1", DateTimeOffset.UtcNow));

        messages[0].Type.ShouldBe("session.resumed");
    }

    [Fact]
    public void Mapper_reports_a_session_without_a_requested_id_as_started()
    {
        var messages = MapAs(null, DateTimeOffset.UtcNow, SessionLine("fresh-id", DateTimeOffset.UtcNow));

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("started");
        messages[0].Content.TryGetProperty("resumed", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_reports_a_session_created_by_the_run_as_started_and_an_old_one_as_resumed()
    {
        var created = new StubCliProcessManager { ExecuteResult = SessionOnlyResult("pi-session-1", DateTimeOffset.UtcNow) };
        var createdMessages = await CollectMessagesAsync(
            CreateProvider(processManager: created),
            new PiOptions { SessionId = "pi-session-1", Model = "mock-model" },
            "hello");
        createdMessages[0].Type.ShouldBe("session.started");
        createdMessages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("started");

        var existing = new StubCliProcessManager { ExecuteResult = SessionOnlyResult("pi-session-1", DateTimeOffset.UtcNow.AddHours(-2)) };
        var existingMessages = await CollectMessagesAsync(
            CreateProvider(processManager: existing),
            new PiOptions { SessionId = "pi-session-1", Model = "mock-model" },
            "hello");
        existingMessages[0].Type.ShouldBe("session.resumed");
    }

    /// <summary>Runs a shell script that prints <paramref name="firstPart" />, waits for a gate file, then prints <paramref name="secondPart" />.</summary>
    private sealed class GatedProcessManager(string firstPart, string secondPart, string directory, string gatePath) : CliProcessManager
    {
        public override async ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            var firstPath = Path.Combine(directory, "first.jsonl");
            var secondPath = Path.Combine(directory, "second.jsonl");
            var scriptPath = Path.Combine(directory, "run.sh");
            await File.WriteAllTextAsync(firstPath, firstPart, cancellationToken);
            await File.WriteAllTextAsync(secondPath, secondPart, cancellationToken);
            await File.WriteAllTextAsync(
                scriptPath,
                // Absolute tool paths: other tests leave PATH=/tmp/bin in the shared test process.
                $"#!/bin/sh\n/bin/cat '{firstPath}'\nwhile [ ! -e '{gatePath}' ]; do /bin/sleep 0.05; done\n/bin/cat '{secondPath}'\nexit 0\n",
                cancellationToken);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return await base.StartAsync(
                new ProcessStartContext
                {
                    ExecutablePath = scriptPath,
                    Arguments = [],
                    WorkingDirectory = directory,
                    OutputEncoding = context.OutputEncoding,
                    InputEncoding = context.InputEncoding
                },
                cancellationToken);
        }
    }

    // GFM table fixture catalog from design.md D1, mirrored in each repository.
    private const string TableT1 = "| Name | Status |\n| --- | --- |\n| alpha | done |\n";

    private const string TableT2 = "Intro line.\n\n| Name | Status |\n| --- | --- |\n| alpha | done |\n\nOutro line.\n";

    private const string TableT3 = "| Item | Count |\n| :--- | ---: |\n| 中文表格 | 3 |\n| trailing cell   | 4   |\n";

    private const string TableT4 = "| - | - |\n| - | - |\n";

    private const string TableT6 = "| Name | Status |\r\n| --- | --- |\r\n| alpha | done |\r\n";

    private const string TableT7 = "- item\n- item\n" + TableT1;

    private static readonly string[] TableFixtureIds = ["T1", "T2", "T3", "T4", "T5-47", "T5-48", "T5-49", "T6", "T7"];

    private static readonly string[] TableSplitStrategies = ["S1", "S2", "S3", "S5-7", "S5-42", "S5-1337"];

    private static readonly IReadOnlyDictionary<string, string> TableFixtures = new Dictionary<string, string>
    {
        ["T1"] = TableT1,
        ["T2"] = TableT2,
        ["T3"] = TableT3,
        ["T4"] = TableT4,
        ["T5-47"] = TableWithRowLength(47),
        ["T5-48"] = TableWithRowLength(48),
        ["T5-49"] = TableWithRowLength(49),
        ["T6"] = TableT6,
        ["T7"] = TableT7,
    };

    /// <summary>A table whose three data rows are each exactly <paramref name="rowLength" /> characters, newline included.</summary>
    private static string TableWithRowLength(int rowLength)
    {
        var builder = new StringBuilder("| Name | Status |\n| --- | --- |\n");
        const string tail = " |\n";
        for (var index = 1; index <= 3; index++)
        {
            var head = $"| r{index} | ";
            builder.Append(head).Append('x', rowLength - head.Length - tail.Length).Append(tail);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Splits a table with the D1 strategies: S1 tokens, S2 characters, S3 cuts around every newline (<c>|\n</c> kept
    /// whole), S4 CR and LF apart, S5-seed seeded random cuts. Each split must concatenate back to the source.
    /// </summary>
    private static string[] SplitTable(string source, string strategy)
    {
        string[] fragments = strategy switch
        {
            "S1" => Regex.Matches(source, @"\s+|\w+|[^\w\s]").Select(static match => match.Value).ToArray(),
            "S2" => source.Select(static character => character.ToString()).ToArray(),
            "S3" => Regex.Matches(source, @"\|\n|\r\n|\n|[^\r\n]+?(?=\|\n|\r?\n|$)").Select(static match => match.Value).ToArray(),
            "S4" => Regex.Matches(source, @"\r|\n|[^\S\r\n]+|\w+|[^\w\s]").Select(static match => match.Value).ToArray(),
            _ when strategy.StartsWith("S5-", StringComparison.Ordinal)
                => SeededCuts(source, int.Parse(strategy["S5-".Length..], CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(strategy), strategy, "Unknown split strategy.")
        };

        string.Concat(fragments).ShouldBe(source);
        return fragments;
    }

    private static string[] SeededCuts(string source, int seed)
    {
        var random = new Random(seed);
        var fragments = new List<string>();
        for (var start = 0; start < source.Length;)
        {
            var length = Math.Min(random.Next(1, 13), source.Length - start);
            fragments.Add(source.Substring(start, length));
            start += length;
        }

        return fragments.ToArray();
    }

    private static IEnumerable<string> TextStreamLines(IEnumerable<string> fragments)
    {
        yield return SessionLine("s-1", DateTimeOffset.UtcNow);
        yield return AssistantStartLine();
        foreach (var fragment in fragments)
        {
            yield return Update(new { type = "text_delta", contentIndex = 0, delta = fragment });
        }
    }

    private static IReadOnlyList<CliMessage> MapStreamedText(string source, IEnumerable<string> fragments)
    {
        var lines = TextStreamLines(fragments).ToList();
        lines.Add(Update(new { type = "text_end", contentIndex = 0, content = source }));
        lines.Add(FinishLine([new { type = "text", text = source }], "stop"));
        return Map(lines.ToArray());
    }

    private static ProcessResult LoadFixture(string name, int exitCode = 0)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pi", $"{name}.jsonl");
        return new ProcessResult(exitCode, File.ReadAllText(path), string.Empty);
    }

    private static ProcessResult SessionOnlyResult(string sessionId, DateTimeOffset createdAt)
    {
        return new ProcessResult(0, string.Join('\n', SessionLine(sessionId, createdAt), FinishLine([new { type = "text", text = "ok" }], "stop")) + "\n", string.Empty);
    }

    private static IReadOnlyList<CliMessage> Map(params string[] lines)
    {
        return MapAs(requestedSessionId: null, processStartedAtUtc: null, lines);
    }

    private static IReadOnlyList<CliMessage> MapAs(string? requestedSessionId, DateTimeOffset? processStartedAtUtc, params string[] lines)
    {
        var result = new ProcessResult(0, string.Join('\n', lines) + "\n", string.Empty);
        return new PiJsonEventMapper().Normalize(result, requestedSessionId, processStartedAtUtc);
    }

    private static string Line(object value) => JsonSerializer.Serialize(value);

    private static string SessionLine(string id, DateTimeOffset createdAt)
    {
        return Line(new { type = "session", version = 3, id, timestamp = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), cwd = "/tmp/pi-project" });
    }

    private static string AssistantStartLine()
    {
        return Line(new
        {
            type = "message_start",
            message = new { role = "assistant", content = Array.Empty<object>(), provider = "mock", model = "mock-model", stopReason = "pending" }
        });
    }

    private static string Update(object assistantMessageEvent)
    {
        return Line(new { type = "message_update", usage = new { input = 0, output = 0 }, assistantMessageEvent });
    }

    /// <summary>The message_end, turn_end and agent_end lines of one assistant message.</summary>
    private static string FinishLine(object[] content, string stopReason)
    {
        var message = new { role = "assistant", content, provider = "mock", model = "mock-model", stopReason, usage = new { input = 20, output = 8, cacheRead = 0, cacheWrite = 0, totalTokens = 28 } };
        return string.Join(
            '\n',
            Line(new { type = "message_end", message }),
            Line(new { type = "turn_end", message, toolResults = Array.Empty<object>() }),
            Line(new { type = "agent_end", messages = new object[] { message }, willRetry = false }));
    }

    private static IReadOnlyList<string?> AssistantTexts(IEnumerable<CliMessage> messages)
    {
        return messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString())
            .ToArray();
    }

    private static bool IsDelta(CliMessage message)
    {
        return message.Content.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.True;
    }

    private static void AssertNoDeltaMarker(IEnumerable<CliMessage> messages)
    {
        foreach (var message in messages)
        {
            message.Content.TryGetProperty("delta", out _).ShouldBeFalse($"{message.Type} must not carry a delta marker in the snapshot format");
        }
    }
}
