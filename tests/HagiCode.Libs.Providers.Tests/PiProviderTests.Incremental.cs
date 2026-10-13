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
        completed.Content.GetProperty("text").GetString().ShouldContain("hello");

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
        messages[^1].Content.GetProperty("text").GetString().ShouldContain("mock upstream rejected");
        messages[^1].Content.GetProperty("stop_reason").GetString().ShouldBe("error");
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
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

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
