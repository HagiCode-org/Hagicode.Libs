// Task 2A.8: the CLI behaviours the 2A features depend on. Tool-call event shape, permission prompts, working-directory
// handling, and exit codes on process failure. Each test is written to fail if HagiCode stops handling the behaviour.
//
// HERMETIC tests use the SYNTHETIC scripted agent (AntigravityScriptedAgent.cs), not agy. The exit-code tests run a local
// /bin/sh stand-in in place of agy. It is not the CLI, but it exercises the real SubprocessAcpTransport exit path. Those
// tests are skipped on Windows, where /bin/sh is absent.
// REAL test (Real_ prefix) needs HAGICODE_REAL_CLI_TESTS=1 and agy on PATH and skips with a reason otherwise. It checks the
// real exit code of an invalid agy invocation. Gaps found here (permission auto-answer, no session/cancel, no per-call
// working-directory check on the CLI side) are recorded in docs/antigravity-feature-matrix.md.
using System.Text.Json;
using System.Text.Json.Nodes;
using HagiCode.Libs.Core.Acp;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Antigravity;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

public sealed class AntigravityCliBehaviourTests
{
    [Test]
    public async Task Tool_call_events_carry_the_call_id_title_kind_and_status_and_pair_with_their_update()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("chat-tool-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "find TODO lines"));

        var call = messages.Single(message => message.Type == "tool.call").Content.GetProperty("update");
        call.GetProperty("toolCallId").GetString().ShouldBe("antigravity-call-1");
        call.GetProperty("title").GetString().ShouldBe("grep");
        call.GetProperty("kind").GetString().ShouldBe("search");
        call.GetProperty("status").GetString().ShouldBe("pending");

        var update = messages.Single(message => message.Type == "tool.update").Content.GetProperty("update");
        update.GetProperty("toolCallId").GetString().ShouldBe(call.GetProperty("toolCallId").GetString());
        update.GetProperty("status").GetString().ShouldBe("completed");
    }

    [Test]
    public async Task The_synthetic_tool_call_fixture_replays_with_the_kinds_its_header_declares()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ToolCalls", "antigravity", "tool-turn.jsonl");
        var lines = File.ReadAllLines(path).Where(static line => !string.IsNullOrWhiteSpace(line)).ToArray();
        var header = JsonNode.Parse(lines[0])!;
        var expected = header["expectedToolKinds"]!.AsObject()
            .ToDictionary(static pair => pair.Key, static pair => pair.Value!.GetValue<string>());

        var agent = new AntigravityScriptedAgent().Enqueue(new AntigravityTurnScript
        {
            Frames = lines.Skip(1).ToArray(),
            Result = JsonNode.Parse("""{"stopReason":"end_turn"}""")
        });
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "tools"));

        var kinds = messages.Where(message => message.Type == "tool.call")
            .ToDictionary(
                message => message.Content.GetProperty("update").GetProperty("toolCallId").GetString()!,
                message => message.Content.GetProperty("update").GetProperty("kind").GetString()!);
        kinds.ShouldBe(expected);
    }

    [Test]
    public async Task Permission_request_is_surfaced_to_the_caller_and_not_silently_dropped()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("permission-request-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        var messages = await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "save notes"));

        var surfaced = messages.Where(message => message.Type == "session.notification")
            .Select(message => message.Content)
            .SingleOrDefault(content => content.GetProperty("method").GetString() == "session/request_permission");
        surfaced.ValueKind.ShouldNotBe(JsonValueKind.Undefined, "the permission request must reach the caller");
        surfaced.GetProperty("params").GetProperty("toolCall").GetProperty("toolCallId").GetString().ShouldBe("antigravity-call-3");
        messages[^1].Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task Permission_request_is_answered_on_the_wire_with_the_default_allow_always_option()
    {
        // Gap, recorded in the matrix: Libs answers permission requests itself (no handler is passed) and picks allow_always.
        // agy's docs say the client shows Allow or Deny, so a user decision is not made here. Change this test when a handler exists.
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.FromFixture("permission-request-turn.jsonl"));
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "save notes"));

        var answer = agent.SentFrames.Single(frame => frame["id"]?.ToJsonString() == "\"perm-1\"" && frame["method"] is null);
        answer["result"]!["outcome"]!["outcome"]!.GetValue<string>().ShouldBe("selected");
        answer["result"]!["outcome"]!["optionId"]!.GetValue<string>().ShouldBe("allow-always");
    }

    [Test]
    public async Task Session_new_carries_the_absolute_working_directory_for_a_relative_option()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn());
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions { WorkingDirectory = "relative-agy-workdir" },
            "hello"));

        agent.RequestsFor("session/new").Single()["params"]!["cwd"]!.GetValue<string>()
            .ShouldBe(Path.GetFullPath("relative-agy-workdir"));
    }

    [Test]
    public async Task Session_new_defaults_the_working_directory_to_the_current_directory()
    {
        var agent = new AntigravityScriptedAgent().Enqueue(AntigravityTurnScript.EndTurn());
        var provider = new ScriptedAntigravityProvider(agent);

        await AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(new AntigravityOptions(), "hello"));

        agent.RequestsFor("session/new").Single()["params"]!["cwd"]!.GetValue<string>()
            .ShouldBe(Directory.GetCurrentDirectory());
    }

    [Test]
    public async Task Process_that_exits_non_zero_before_answering_surfaces_its_exit_code_and_stderr()
    {
        SkipUnlessPosixShell();
        // Reads the initialize request first, so the write succeeds and the failure comes from the exit, not a broken pipe.
        var provider = new ShellStubAntigravityProvider("read -r ignored; echo 'agy stub failure' >&2; exit 3");

        var error = await Should.ThrowAsync<InvalidOperationException>(() => AntigravityTestHelpers.CollectAsync(provider.ExecuteAsync(
            new AntigravityOptions(),
            "hello")));

        error.Message.ShouldContain("code 3");
        error.Message.ShouldContain("agy stub failure");
    }

    [Test]
    public async Task Process_that_exits_non_zero_never_reports_a_completed_turn()
    {
        SkipUnlessPosixShell();
        var provider = new ShellStubAntigravityProvider("read -r ignored; exit 5");

        var messages = new List<CliMessage>();
        var error = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(new AntigravityOptions(), "hello"))
            {
                messages.Add(message);
            }
        });

        error.Message.ShouldContain("code 5");
        messages.Any(message => message.Type == "terminal.completed").ShouldBeFalse();
    }

    [Test]
    public async Task Process_that_exits_before_reading_input_still_fails_the_turn()
    {
        // Gap, recorded in the matrix: when the process is gone before the first write, the write fails with an IOException
        // and the exit code is not carried. The test only requires that the turn fails and does not complete.
        SkipUnlessPosixShell();
        var provider = new ShellStubAntigravityProvider("exit 7");

        var messages = new List<CliMessage>();
        Exception? thrown = null;
        try
        {
            await foreach (var message in provider.ExecuteAsync(new AntigravityOptions(), "hello"))
            {
                messages.Add(message);
            }
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.ShouldNotBeNull("the turn must fail when the process is gone before it answers");
        messages.Any(message => message.Type == "terminal.completed").ShouldBeFalse();
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Real_invalid_agy_invocation_exits_non_zero_with_a_message()
    {
        var agyPath = AntigravityRealCli.RequireAgy();

        var result = await new CliProcessManager().ExecuteAsync(new ProcessStartContext
        {
            ExecutablePath = agyPath,
            Arguments = ["acp", "--stdio", "--hagicode-probe-unknown-flag"],
            WorkingDirectory = Directory.GetCurrentDirectory(),
            Timeout = TimeSpan.FromSeconds(60)
        });

        result.TimedOut.ShouldBeFalse("agy must exit on an unknown flag rather than wait for a client");
        result.ExitCode.ShouldNotBe(0);
        (result.StandardError + result.StandardOutput).Trim().ShouldNotBeNullOrWhiteSpace();
    }

    private static void SkipUnlessPosixShell()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Ignore("The exit-code stand-in uses /bin/sh, which is not available on Windows.");
        }
    }

    /// <summary>Runs <c>/bin/sh -c &lt;script&gt;</c> in place of agy, through the real subprocess transport.</summary>
    private sealed class ShellStubAntigravityProvider(string script)
        : AntigravityProvider(new StubAgyExecutableResolver(), new CliProcessManager(), null)
    {
        private readonly CliProcessManager _processManager = new();

        protected override IAcpTransport CreateAcpTransport(ProcessStartContext startContext)
        {
            return new SubprocessAcpTransport(_processManager, startContext with
            {
                ExecutablePath = "/bin/sh",
                Arguments = ["-c", script]
            });
        }
    }
}
