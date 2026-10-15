using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Omp;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Interactive questions on OMP: the rpc-ui path that an OmpOptions.UserInputHandler switches on. The conversation is scripted:
// Fixtures/Omp holds real omp 18.10.4 output (claude-haiku-5-5 through omniroute, 2026-10-14), and the scripted process plays omp
// (it answers set_ask_dialog, get_state and prompt the way the capture did and reacts to the host's writes), so none of these
// tests needs omp, credentials or a model.
public sealed class OmpProviderUserQuestionTests
{
    private const string ColourQuestion = "Which colour do you prefer?";
    private const string FeaturesQuestion = "Which features do you want?";
    private const string TwoQuestionsDialogId = "15a576d263f8814c";
    private const string TwoQuestionsSessionId = "01a138a5-f208-77ef-a6c0-c389eb9c97fd";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private static readonly Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> NoAnswer =
        static (_, _) => Task.FromResult<OmpUserInputResponse?>(null);

    // ---------------------------------------------------------------- default path regression guard (3.1)

    [Fact]
    public async Task ExecuteAsync_without_a_handler_keeps_the_print_command_sends_no_rpc_command_closes_stdin_and_completes_on_exit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new ScratchDirectory();
        var stdoutPath = Path.Combine(directory.Path, "stdout.jsonl");
        var stdinPath = Path.Combine(directory.Path, "stdin.txt");
        await File.WriteAllLinesAsync(stdoutPath,
        [
            """{"type":"session","version":3,"id":"omp-session-1","timestamp":"2026-10-14T04:25:20.406Z","cwd":"/tmp/omp-project"}""",
            """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"hi"}],"provider":"mock","model":"mock-model","stopReason":"stop"},"toolResults":[]}""",
            """{"type":"agent_end","messages":[]}"""
        ]);

        // The script prints only after its standard input ended and records what it was sent, so a provider that left stdin
        // open would hang here and one that wrote an RPC command would leave it in the record.
        var manager = new ScriptProcessManager(
            directory.WriteScript("print.sh", "/bin/cat > \"$2\"\n/bin/cat \"$1\"\nexit 0\n"),
            [stdoutPath, stdinPath]);
        var provider = new OmpProvider(new StubExecutableResolver(), manager);

        var messages = await CollectAsync(provider, new OmpOptions { Model = "mock-model", NoSession = true }, "Say hi");

        manager.LastContext.ShouldNotBeNull();
        manager.LastContext.Arguments.ShouldBe(["--mode", "json", "--print", "--model", "mock-model", "--no-session", "Say hi"]);
        manager.Starts.ShouldBe(1);
        (await File.ReadAllTextAsync(stdinPath)).ShouldBeEmpty();
        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.completed"]);
        messages.ShouldNotContain(static message => message.Type == OmpUserInputRequest.MessageType);
    }

    [Fact]
    public async Task ExecuteAsync_with_every_tool_disabled_ignores_the_handler()
    {
        var provider = new ScriptedProvider(new ScriptedOmpProcess());
        var options = new OmpOptions
        {
            Model = "mock-model",
            NoSession = true,
            DisableAllTools = true,
            UserInputHandler = NoAnswer
        };

        var messages = await CollectAsync(provider, options, "Ask me");

        provider.RpcStarts.ShouldBe(0);
        provider.PrintContext.ShouldNotBeNull();
        provider.PrintContext.Arguments.ShouldBe(
            ["--mode", "json", "--print", "--model", "mock-model", "--no-session", "--no-tools", "Ask me"]);
        messages.ShouldHaveSingleItem().Type.ShouldBe("terminal.completed");
    }

    // ---------------------------------------------------------------- RPC command line and handshake (3.2)

    [Fact]
    public void BuildRpcCommandArguments_selects_rpc_ui_mode_keeps_every_option_and_sends_no_prompt()
    {
        var provider = new ScriptedProvider(new ScriptedOmpProcess());

        var arguments = provider.BuildRpcCommandArguments(new OmpOptions
        {
            ExecutablePath = "/custom/omp",
            WorkingDirectory = "/tmp/omp-project",
            Provider = "omniroute",
            Model = "glm/glm-4.7",
            SystemPrompt = "You are a trip planner.",
            AppendSystemPrompts = ["Keep answers terse."],
            SessionId = "omp-session-1",
            AllowedTools = ["read", "grep"],
            Thinking = "minimal",
            ExtraArguments = ["--verbose", "   ", "--offline"],
            UserInputHandler = NoAnswer
        });

        arguments.ShouldBe(
        [
            "--mode",
            "rpc-ui",
            "--model",
            "omniroute/glm/glm-4.7",
            "--system-prompt",
            "You are a trip planner.",
            "--append-system-prompt",
            "Keep answers terse.",
            "--thinking",
            "minimal",
            "--resume",
            "omp-session-1",
            "--tools",
            "read,grep,ask",
            "--verbose",
            "--offline"
        ]);
        arguments.ShouldNotContain("--print");
        arguments.ShouldNotContain("--no-ui");
        arguments.ShouldNotContain("-e");
        arguments.ShouldNotContain("json");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void BuildRpcCommandArguments_differs_from_the_print_command_only_in_mode_allowlist_and_prompt(bool restrictTools, bool noSession)
    {
        var provider = new ScriptedProvider(new ScriptedOmpProcess());
        var options = new OmpOptions
        {
            Provider = "anthropic",
            Model = "claude-haiku",
            SystemPrompt = "System",
            AppendSystemPrompts = ["One", "Two"],
            SessionId = "s-1",
            NoSession = noSession,
            Thinking = "high",
            ExtraArguments = ["--offline"],
            AllowedTools = restrictTools ? ["read"] : [],
            UserInputHandler = NoAnswer
        };

        var print = provider.BuildCommandArguments(options, "the prompt").ToList();
        var rpc = provider.BuildRpcCommandArguments(options).ToList();

        // Same options in the same order once the mode flags, the prompt and the question tool are taken out.
        print.RemoveRange(0, 3);
        print.RemoveAt(print.Count - 1);
        rpc.RemoveRange(0, 2);
        var tools = rpc.IndexOf("--tools");
        if (restrictTools)
        {
            rpc[tools + 1].ShouldBe("read,ask");
            rpc[tools + 1] = "read";
        }
        else
        {
            tools.ShouldBe(-1);
        }

        rpc.ShouldBe(print);
    }

    [Fact]
    public void BuildRpcCommandArguments_adds_the_question_tool_to_an_allowlist_only_once_and_never_to_an_empty_one()
    {
        var provider = new ScriptedProvider(new ScriptedOmpProcess());

        var present = provider.BuildRpcCommandArguments(new OmpOptions { AllowedTools = [" read ", "ask"], UserInputHandler = NoAnswer });
        var blank = provider.BuildRpcCommandArguments(new OmpOptions { AllowedTools = ["  ", ""], UserInputHandler = NoAnswer });

        present[present.ToList().IndexOf("--tools") + 1].ShouldBe("read,ask");
        blank.ShouldNotContain("--tools");
    }

    [Fact]
    public async Task ExecuteAsync_starts_omp_in_rpc_ui_mode_and_sends_the_prompt_as_a_command_after_the_handshake()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);
        var provider = new ScriptedProvider(process);

        await CollectAsync(
            provider,
            new OmpOptions
            {
                WorkingDirectory = "/tmp",
                Model = "omniroute/claude/claude-haiku-5-5",
                NoSession = true,
                AllowedTools = ["read"],
                UserInputHandler = AnswerAll("Blue", "Dark theme, Offline mode")
            },
            "Ask me about colours");

        provider.RpcContext.ShouldNotBeNull();
        provider.RpcContext.Arguments.ShouldBe(
            ["--mode", "rpc-ui", "--model", "omniroute/claude/claude-haiku-5-5", "--no-session", "--tools", "read,ask"]);
        provider.PrintContext.ShouldBeNull();

        var written = process.Written.Select(ScriptedOmpProcess.Parse).ToList();
        written.Select(static command => command.Type).ShouldBe(["set_ask_dialog", "get_state", "prompt", "extension_ui_response"]);
        written[0].Root.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        written[2].Root.GetProperty("message").GetString().ShouldBe("Ask me about colours");

        // Each command was written only after OMP answered the one before it.
        process.Log.IndexOf("write:get_state").ShouldBeGreaterThan(process.Log.IndexOf("emit:response:set_ask_dialog"));
        process.Log.IndexOf("write:prompt").ShouldBeGreaterThan(process.Log.IndexOf("emit:response:get_state"));
    }

    [Fact]
    public async Task Nothing_but_set_ask_dialog_is_written_until_omp_answers_it()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);
        process.HoldReply = static command => command.Type == "set_ask_dialog";
        var provider = new ScriptedProvider(process);

        var run = CollectAsync(provider, RpcOptions(AnswerAll("Blue", "Dark theme")), "Ask me");

        // The provider wrote set_ask_dialog, read the startup frames and is now blocked reading: nothing else may have been
        // written.
        await WaitUntilAsync(() => process.Written.Count >= 1 && process.ReaderWaiting);
        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldBe(["set_ask_dialog"]);

        process.ReleaseHeldReplies();
        var messages = await run;
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ExecuteAsync_rejects_a_non_positive_question_timeout_before_starting_a_process(int seconds)
    {
        var provider = new ScriptedProvider(new ScriptedOmpProcess());
        var options = new OmpOptions { UserInputHandler = NoAnswer, QuestionTimeout = TimeSpan.FromSeconds(seconds) };

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await CollectAsync(provider, options, "Ask me"));

        exception.ParamName.ShouldBe("options");
        provider.RpcStarts.ShouldBe(0);
        provider.PrintContext.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_accepts_an_infinite_question_timeout()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions(AnswerAll("Blue", "Dark theme")) with { QuestionTimeout = Timeout.InfiniteTimeSpan },
            "Ask me");

        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task ExecuteAsync_ignores_the_question_timeout_when_there_is_no_handler()
    {
        var provider = new ScriptedProvider(new ScriptedOmpProcess());

        var messages = await CollectAsync(provider, new OmpOptions { QuestionTimeout = TimeSpan.Zero }, "Say hi");

        provider.RpcStarts.ShouldBe(0);
        messages.ShouldHaveSingleItem().Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task A_build_that_rejects_set_ask_dialog_falls_back_to_the_print_path_without_a_prompt_and_logs_a_warning()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("set-ask-dialog-unsupported")).Attach(process);
        var logger = new CapturingLogger();
        var provider = new ScriptedProvider(process, logger);
        var handlerCalls = 0;

        var messages = await CollectAsync(
            provider,
            RpcOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<OmpUserInputResponse?>(null);
            }),
            "Ask me");

        // The RPC process got set_ask_dialog only and was shut down; the request ran the way it runs without a handler.
        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldBe(["set_ask_dialog"]);
        process.InputClosed.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
        process.Disposed.ShouldBeTrue();
        provider.PrintContext.ShouldNotBeNull();
        provider.PrintContext.Arguments.ShouldBe(["--mode", "json", "--print", "--model", "mock-model", "--no-session", "Ask me"]);
        messages.ShouldHaveSingleItem().Content.GetProperty("text").GetString().ShouldBe("print path");
        handlerCalls.ShouldBe(0);

        var warning = logger.Entries.ShouldHaveSingleItem();
        warning.Level.ShouldBe(LogLevel.Warning);
        warning.Message.ShouldContain("omp/0.0.0-test");
        warning.Message.ShouldContain("set_ask_dialog");
        warning.Message.ShouldContain("Unknown command: set_ask_dialog");
    }

    // What omp 18.10.4 writes to standard error before its ready frame for an unknown model and for a --resume id that does
    // not exist (it does not start a new session under that id).
    [Theory]
    [InlineData("Model \"omniroute/claude/claude-nonexistent-9\" not found\n\nRun `omp models find <pattern>` to search, or `omp models` to list all.\n")]
    [InlineData("Error: Session \"01a1ffff-0000-7000-8000-000000000000\" not found.\nRun `omp --resume` without an argument to pick from recent sessions, or `omp` to start a new one.\n")]
    public async Task A_startup_failure_before_the_handshake_is_reported_not_retried(string standardError)
    {
        var process = new ScriptedOmpProcess();
        process.OnStart = omp => omp.Exit(1, standardError);
        var provider = new ScriptedProvider(process);

        var messages = await CollectAsync(provider, RpcOptions(NoAnswer) with { NoSession = false, SessionId = "01a1ffff-0000-7000-8000-000000000000" }, "Ask me");

        provider.PrintContext.ShouldBeNull();
        var terminal = messages.ShouldHaveSingleItem();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("not found");
        terminal.Content.GetProperty("stderr").GetString().ShouldBe(standardError.Trim());
        terminal.Content.GetProperty("exit_code").GetInt32().ShouldBe(1);
    }

    // ---------------------------------------------------------------- session id and completion (3.3)

    [Fact]
    public async Task The_session_id_comes_from_get_state_and_is_reported_as_started()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions(AnswerAll("Blue", "Dark theme")) with { WorkingDirectory = "/tmp/omp-project" },
            "Ask me");

        var session = messages[0];
        session.Type.ShouldBe("session.started");
        session.Content.GetProperty("session_id").GetString().ShouldBe(TwoQuestionsSessionId);
        session.Content.GetProperty("sessionId").GetString().ShouldBe(TwoQuestionsSessionId);
        session.Content.GetProperty("resumeMode").GetString().ShouldBe("started");
        session.Content.GetProperty("cwd").GetString().ShouldBe("/tmp/omp-project");
        messages.Count(static message => message.Type.StartsWith("session.", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_requested_session_that_omp_reports_back_is_reported_as_resumed()
    {
        const string ResumedSessionId = "01a138a8-ac6c-728d-8bb2-9d2994728322";
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("resumed-session")).Attach(process);
        var provider = new ScriptedProvider(process);

        var messages = await CollectAsync(
            provider,
            RpcOptions(NoAnswer) with { NoSession = false, SessionId = ResumedSessionId },
            "What word did I ask you to remember?");

        provider.RpcContext!.Arguments.ShouldContain("--resume");
        messages[0].Type.ShouldBe("session.resumed");
        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe(ResumedSessionId);
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
        AssistantText(messages).ShouldContain("WORD=KIWI");
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task A_requested_session_that_omp_does_not_report_back_is_reported_as_restarted()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions(AnswerAll("Blue", "Dark theme")) with { NoSession = false, SessionId = "another-session" },
            "Ask me");

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("restarted");
    }

    [Fact]
    public async Task The_run_completes_on_session_settled_then_closes_standard_input_and_waits_for_the_exit()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(AnswerAll("Blue", "Dark theme")), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.completed");
        terminal.Content.GetProperty("session_id").GetString().ShouldBe(TwoQuestionsSessionId);
        messages.Count(static message => message.Type.StartsWith("terminal.", StringComparison.Ordinal)).ShouldBe(1);
        process.Log.IndexOf("close-input").ShouldBeGreaterThan(process.Log.IndexOf("emit:session_settled"));
        process.InputClosed.ShouldBeTrue();
        process.Stopped.ShouldBeFalse();
        process.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_process_that_does_not_exit_after_the_run_settled_is_stopped_and_the_run_still_completes()
    {
        var process = new ScriptedOmpProcess { ExitOnInputClose = false };
        Scenario.From(Fixture("ask-two-questions")).Attach(process);
        var provider = new ScriptedProvider(process) { RpcExitTimeout = TimeSpan.FromMilliseconds(200) };

        var messages = await CollectAsync(provider, RpcOptions(AnswerAll("Blue", "Dark theme")), "Ask me");

        messages.Last().Type.ShouldBe("terminal.completed");
        process.Stopped.ShouldBeTrue();
    }

    [Fact]
    public async Task A_prompt_result_before_the_session_settled_keeps_the_run_open_for_the_follow_up_events()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("background-follow-up")).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Start a background job");

        // prompt_result arrived after STARTED with sessionSettled false; the background result woke the agent, which answered
        // GOT=bg-done, and only session_settled ended the run.
        var assistant = messages.Where(static message => message.Type == "assistant").Select(static message => message.Content.GetProperty("text").GetString()).ToList();
        string.Concat(assistant).ShouldContain("STARTED");
        string.Concat(assistant).ShouldContain("GOT=bg-done");
        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.completed");
        messages.Count(static message => message.Type.StartsWith("terminal.", StringComparison.Ordinal)).ShouldBe(1);
        process.Log.IndexOf("close-input").ShouldBeGreaterThan(process.Log.IndexOf("emit:session_settled"));
    }

    [Fact]
    public async Task An_error_prompt_result_is_a_terminal_failure_with_omps_error_text()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("error-turn")).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Reply with exactly: PONG");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        var text = terminal.Content.GetProperty("text").GetString();
        text.ShouldStartWith("400 [400]: max_tokens: 163072 > 128000");
        text.ShouldNotContain("raw-http-request");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("error");
        terminal.Content.GetProperty("source_event_type").GetString().ShouldBe("prompt_result");
        terminal.Content.GetProperty("http_status").GetInt32().ShouldBe(400);
        terminal.Content.GetProperty("provider").GetString().ShouldBe("omniroute");
        terminal.Content.GetProperty("model").GetString().ShouldBe("claude/claude-haiku-5-5");
        terminal.Content.GetProperty("retryable").GetBoolean().ShouldBeFalse();
        terminal.Content.GetProperty("session_id").GetString().ShouldBe("01a138a7-b361-77f8-b90a-9bfc43833d1c");
        messages.Count(static message => message.Type.StartsWith("terminal.", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Fact]
    public async Task A_rejected_prompt_becomes_a_terminal_failure_that_keeps_the_error_text_and_shuts_omp_down()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("rejected-prompt")).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        messages[1].Content.GetProperty("text").GetString().ShouldBe("undefined is not an object (evaluating 'R.trim')");
        messages[1].Content.GetProperty("source_event_type").GetString().ShouldBe("rpc_prompt");
        messages[1].Content.GetProperty("session_id").GetString().ShouldBe("01a138a7-49b4-71a1-a267-c689aa85ff4e");
        process.InputClosed.ShouldBeTrue();
        process.Stopped.ShouldBeFalse();
    }

    [Fact]
    public async Task A_rejected_get_state_becomes_a_terminal_failure_without_sending_the_prompt()
    {
        var process = new ScriptedOmpProcess();
        process.OnWrite = static (omp, command) =>
        {
            if (command.Type == "set_ask_dialog")
            {
                omp.Emit("""{"id":"hagicode-set-ask-dialog","type":"response","command":"set_ask_dialog","success":true,"data":{"enabled":true}}""");
            }
            else if (command.Type == "get_state")
            {
                omp.Emit("""{"id":"hagicode-get-state","type":"response","command":"get_state","success":false,"error":"state unavailable"}""");
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.ShouldHaveSingleItem();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("state unavailable");
        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldNotContain("prompt");
    }

    [Fact]
    public async Task A_process_that_ends_before_the_run_settled_fails_with_its_exit_code_and_standard_error()
    {
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("ask-two-questions"));
        process.OnWrite = (omp, command) =>
        {
            if (command.Type == "set_ask_dialog")
            {
                omp.Emit(scenario.AskDialogReply);
            }
            else if (command.Type == "get_state")
            {
                omp.Emit(scenario.StateReply);
                omp.Exit(3, "omp: out of memory\n");
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("exit_code").GetInt32().ShouldBe(3);
        var terminalStderr = terminal.Content.GetProperty("stderr").GetString();
        terminalStderr.ShouldNotBeNull();
        terminalStderr.ShouldContain("omp: out of memory");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("omp: out of memory");
    }

    [Fact]
    public async Task A_process_that_exits_with_code_zero_before_settling_is_not_reported_as_completed()
    {
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("ask-two-questions"));
        process.OnWrite = (omp, command) =>
        {
            if (command.Type == "set_ask_dialog")
            {
                omp.Emit(scenario.AskDialogReply);
            }
            else if (command.Type == "get_state")
            {
                omp.Emit(scenario.StateReply);
            }
            else if (command.Type == "prompt")
            {
                omp.Emit(scenario.UntilDialog.Take(4));
                omp.Exit(0);
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("before the run settled");
        terminal.Content.GetProperty("session_id").GetString().ShouldBe(TwoQuestionsSessionId);
    }

    [Fact]
    public async Task A_line_that_is_not_json_after_which_omp_ends_is_a_terminal_failure_that_keeps_the_line()
    {
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("ask-two-questions"));
        process.OnWrite = (omp, command) =>
        {
            if (command.Type == "set_ask_dialog")
            {
                omp.Emit(scenario.AskDialogReply);
            }
            else if (command.Type == "get_state")
            {
                omp.Emit(scenario.StateReply);
            }
            else if (command.Type == "prompt")
            {
                omp.Emit("Segmentation fault (core dumped)");
                omp.Exit(0);
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("before the run settled");
        terminalText.ShouldContain("Segmentation fault (core dumped)");
        terminal.Content.GetProperty("invalid_output_lines")[0].GetString().ShouldBe("Segmentation fault (core dumped)");
    }

    [Theory]
    [InlineData("ask-two-questions")]
    [InlineData("tool-turn")]
    [InlineData("background-follow-up")]
    public async Task Events_are_normalized_exactly_as_the_json_path_normalizes_them(string fixtureName)
    {
        var capture = Fixture(fixtureName);
        var process = new ScriptedOmpProcess();
        Scenario.From(capture).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(AnswerAll("Blue", "Dark theme, Offline mode")), "Ask me");

        // The JSON path sees the same events plus the session header RPC lacks, process exit instead of session_settled, and
        // text and thinking message_update events without the stale message snapshot RPC adds to them.
        var expected = new List<CliMessage>();
        var state = new OmpJsonEventMapper().CreateStreamingState(null);
        var sessionId = Scenario.SessionIdOf(capture);
        expected.AddRange(state.ProcessOutputLine($$"""{"type":"session","id":"{{sessionId}}"}"""));
        foreach (var line in capture)
        {
            if (ScriptedOmpProcess.Parse(line).Type is "ready" or "response" or "extension_ui_request" or "available_commands_update"
                or "prompt_result" or "session_settled")
            {
                continue;
            }

            expected.AddRange(state.ProcessOutputLine(WithoutStaleSnapshot(line)));
        }

        expected.AddRange(state.Complete(0, string.Empty));

        var actual = messages.Where(static message => message.Type != OmpUserInputRequest.MessageType).ToList();
        actual.Select(static message => message.Type).ShouldBe(expected.Select(static message => message.Type));
        actual.Select(static message => message.Content.GetRawText()).ShouldBe(expected.Select(static message => message.Content.GetRawText()));
    }

    [Fact]
    public async Task Text_thinking_and_tool_events_become_the_same_shared_messages_on_both_paths()
    {
        // Thinking does not reach this omniroute model's stream, so these are delta-only frames in the shape --mode json sends.
        string[] events =
        [
            """{"type":"turn_start"}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","contentIndex":0,"delta":"Need to "}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"thinking_delta","contentIndex":0,"delta":"run echo."}}""",
            """{"type":"message_update","assistantMessageEvent":{"type":"text_delta","contentIndex":1,"delta":"Done"}}""",
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"thinking","thinking":"Need to run echo."},{"type":"text","text":"Done"}],"provider":"omniroute","model":"m","stopReason":"stop"}}""",
            """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"Done"}],"provider":"omniroute","model":"m","stopReason":"stop"},"toolResults":[]}""",
            """{"type":"agent_end","messages":[]}"""
        ];
        var process = new ScriptedOmpProcess();
        process.OnWrite = (omp, command) =>
        {
            switch (command.Type)
            {
                case "set_ask_dialog":
                    omp.Emit("""{"id":"hagicode-set-ask-dialog","type":"response","command":"set_ask_dialog","success":true,"data":{"enabled":true}}""");
                    break;
                case "get_state":
                    omp.Emit("""{"id":"hagicode-get-state","type":"response","command":"get_state","success":true,"data":{"sessionId":"s-think"}}""");
                    break;
                case "prompt":
                    omp.Emit("""{"id":"hagicode-prompt","type":"response","command":"prompt","success":true}""");
                    omp.Emit(events);
                    omp.Emit("""{"type":"prompt_result","id":"hagicode-prompt","agentInvoked":true,"status":"completed","sessionSettled":true}""");
                    omp.Emit("""{"type":"session_settled"}""");
                    break;
            }
        };

        var rpc = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Think");

        var state = new OmpJsonEventMapper().CreateStreamingState(null);
        var json = new List<CliMessage>(state.ProcessOutputLine("""{"type":"session","id":"s-think"}"""));
        foreach (var line in events)
        {
            json.AddRange(state.ProcessOutputLine(line));
        }

        json.AddRange(state.Complete(0, string.Empty));
        rpc.Select(static message => message.Type).ShouldBe(["session.started", "assistant", "assistant.thought", "terminal.completed"]);
        rpc.Select(static message => message.Content.GetRawText()).ShouldBe(json.Select(static message => message.Content.GetRawText()));
    }

    [Fact]
    public async Task An_rpc_ui_run_streams_the_text_once_and_names_the_tool_call_from_its_first_message()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("tool-turn")).Attach(process);

        var rpc = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Run echo");

        // Two real runs of one prompt (rpc-ui and --mode json). rpc-ui attaches a message snapshot to message_update that
        // already holds text its deltas have not delivered yet; the text must still arrive exactly once.
        var state = new OmpJsonEventMapper().CreateStreamingState(null);
        var json = new List<CliMessage>();
        foreach (var line in Fixture("tool-turn-json"))
        {
            json.AddRange(state.ProcessOutputLine(line));
        }

        json.AddRange(state.Complete(0, string.Empty));
        AssistantText(rpc).ShouldBe("DONE=hagicode-probe");
        AssistantText(json).ShouldBe("DONE=hagicode-probe");
        rpc.Count(static message => message.Type == "assistant").ShouldBe(json.Count(static message => message.Type == "assistant"));

        // The tool call keeps its snapshot, so every lifecycle message carries the real id and tool name; the delta-only JSON
        // stream starts with a placeholder until toolcall_end names the call.
        var tools = rpc.Where(static message => message.Type.StartsWith("tool.", StringComparison.Ordinal)).ToList();
        tools.Select(static message => message.Type).ShouldBe(["tool.call", "tool.update", "tool.completed"]);
        tools.ShouldAllBe(static message => message.Content.GetProperty("tool_call_id").GetString() == "toolu_01PAHx8Azmtj3HCLVFYE775u");
        tools.ShouldAllBe(static message => message.Content.GetProperty("tool_name").GetString() == "bash");
        // kind is the classified vocabulary value; title keeps the name OMP reported.
        tools.ShouldAllBe(static message => message.Content.GetProperty("update").GetProperty("kind").GetString() == "shell");
        tools.ShouldAllBe(static message => message.Content.GetProperty("update").GetProperty("title").GetString() == "bash");
        tools[1].Content.GetProperty("update").GetProperty("rawInput").GetProperty("command").GetString().ShouldBe("echo hagicode-probe");
        json.First(static message => message.Type == "tool.call").Content.GetProperty("tool_name").GetString().ShouldBe("tool_call");
        rpc.Last().Type.ShouldBe("terminal.completed");
    }

    // ---------------------------------------------------------------- ask routing and outcomes (3.4)

    [Fact]
    public async Task An_ask_request_reaches_the_handler_with_every_field_and_the_caller_gets_the_question_message_in_order()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);
        var requests = new List<OmpUserInputRequest>();
        var options = RpcOptions((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult<OmpUserInputResponse?>(Answers((ColourQuestion, ["Blue"]), (FeaturesQuestion, ["Dark theme"])));
        });

        var messages = await CollectAsync(new ScriptedProvider(process), options, "Ask me");

        var request = requests.ShouldHaveSingleItem();
        request.RequestId.ShouldBe(TwoQuestionsDialogId);
        request.Questions.Count.ShouldBe(2);
        var colour = request.Questions[0];
        colour.Id.ShouldBe("colour");
        colour.Question.ShouldBe(ColourQuestion);
        colour.Header.ShouldBe("Colour");
        colour.MultiSelect.ShouldBeFalse();
        colour.Options.ShouldBe([new OmpUserQuestionOption("Red", "Warm"), new OmpUserQuestionOption("Blue", "Cool")]);
        var features = request.Questions[1];
        features.Id.ShouldBe("features");
        features.Question.ShouldBe(FeaturesQuestion);
        features.Header.ShouldBe("Features");
        features.MultiSelect.ShouldBeTrue();
        features.Options.ShouldBe(
        [
            new OmpUserQuestionOption("Dark theme", "Dark colours"),
            new OmpUserQuestionOption("Mobile layout", "Small screens"),
            new OmpUserQuestionOption("Offline mode", "No network")
        ]);

        var questionMessage = messages.Single(static message => message.Type == OmpUserInputRequest.MessageType);
        OmpUserInputRequest.TryFromCliMessage(questionMessage, out var parsed).ShouldBeTrue();
        parsed.RequestId.ShouldBe(request.RequestId);
        parsed.Questions.Select(static question => question.Id).ShouldBe(["colour", "features"]);

        // In stream order: after the agent's ask tool call, before the tool's result and the agent's reply.
        var types = messages.Select(static message => message.Type).ToList();
        types.IndexOf("tool.call").ShouldBeLessThan(types.IndexOf(OmpUserInputRequest.MessageType));
        types.IndexOf(OmpUserInputRequest.MessageType).ShouldBeLessThan(types.IndexOf("tool.completed"));
        types.IndexOf("tool.completed").ShouldBeLessThan(types.LastIndexOf("assistant"));
        var toolCall = messages.First(static message => message.Type == "tool.call").Content;
        toolCall.GetProperty("tool_name").GetString().ShouldBe(OmpUserInputRequest.QuestionToolName);
        toolCall.GetProperty("tool_call_id").GetString().ShouldBe("toolu_012f7RsvnJqCa5iaodytAExt");
    }

    [Fact]
    public async Task The_answers_are_written_once_per_question_in_request_order_as_omp_validates_them()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, _) => Task.FromResult<OmpUserInputResponse?>(
                Answers((FeaturesQuestion, ["Dark theme", "Offline mode"]), (ColourQuestion, ["Blue"])))),
            "Ask me");

        // This is the frame the real run was fed; OMP answered it with "colour: Blue / features: [Dark theme, Offline mode]".
        var response = process.Written.Select(ScriptedOmpProcess.Parse).Single(static command => command.Type == "extension_ui_response");
        process.Written.Single(static line => line.Contains("extension_ui_response", StringComparison.Ordinal)).ShouldBe(
            """{"type":"extension_ui_response","id":"15a576d263f8814c","answers":[{"id":"colour","selectedOptions":["Blue"]},{"id":"features","selectedOptions":["Dark theme","Offline mode"]}]}""");
        response.Root.TryGetProperty("cancelled", out _).ShouldBeFalse();
        AssistantText(messages).ShouldBe("ANSWER=Blue|Dark theme, Offline mode");
    }

    [Fact]
    public async Task Free_text_answers_are_sent_as_custom_input_with_no_selected_option()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-free-text")).Attach(process);

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, _) => Task.FromResult<OmpUserInputResponse?>(
                Answers((ColourQuestion, ["Green, please"]), (FeaturesQuestion, ["Something else, maybe"])))),
            "Ask me");

        process.Written.Single(static line => line.Contains("extension_ui_response", StringComparison.Ordinal)).ShouldBe(
            """{"type":"extension_ui_response","id":"15a576fa6c093806","answers":[{"id":"colour","selectedOptions":[],"customInput":"Green, please"},{"id":"features","selectedOptions":[],"customInput":"Something else, maybe"}]}""");
        AssistantText(messages).ShouldContain("ANSWER=Green, please|Something else, maybe");
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task Output_keeps_streaming_while_a_question_is_open()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-two-questions")).Attach(process);
        var answer = new TaskCompletionSource<OmpUserInputResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(process);
        var options = RpcOptions((_, _) =>
        {
            handlerStarted.TrySetResult();
            return answer.Task;
        });

        var received = Channel.CreateUnbounded<CliMessage>();
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(options, "Ask me"))
            {
                await received.Writer.WriteAsync(message);
            }

            received.Writer.Complete();
        });

        await handlerStarted.Task.WaitAsync(TestTimeout);
        await ReadUntilAsync(received.Reader, static message => message.Type == OmpUserInputRequest.MessageType);

        // The question is open and the handler has not answered, yet a later assistant message gets through.
        process.Emit(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"still streaming"}],"stopReason":"toolUse"}}""");
        var streamed = await ReadUntilAsync(received.Reader, static message => message.Type == "assistant");
        streamed.Content.GetProperty("text").GetString().ShouldBe("still streaming");
        answer.Task.IsCompleted.ShouldBeFalse();
        process.Written.Select(ScriptedOmpProcess.Parse).ShouldNotContain(static command => command.Type == "extension_ui_response");

        answer.SetResult(Answers((ColourQuestion, ["Blue"]), (FeaturesQuestion, ["Dark theme"])));
        await run.WaitAsync(TestTimeout);
        process.Written.Select(ScriptedOmpProcess.Parse).Count(static command => command.Type == "extension_ui_response").ShouldBe(1);
    }

    [Fact]
    public async Task Foreign_dialogs_are_cancelled_fire_and_forget_requests_get_no_response_and_the_question_is_still_asked()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("foreign-dialogs")).Attach(process);
        var requests = new List<OmpUserInputRequest>();

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<OmpUserInputResponse?>(Answers((ColourQuestion, ["Blue"])));
            }),
            "Step 1: call user_dialogs");

        requests.ShouldHaveSingleItem().RequestId.ShouldBe("15a577e68c6cbc2f");
        messages.Count(static message => message.Type == OmpUserInputRequest.MessageType).ShouldBe(1);

        // select, confirm, input and editor of the user extension, in the order omp asked them, then the answer to ask.
        var responses = process.Written.Select(ScriptedOmpProcess.Parse).Where(static command => command.Type == "extension_ui_response").ToList();
        responses.Select(static response => response.Id).ShouldBe(
            ["15a577e4e2acbc2b", "15a577e4e2ecbc2c", "15a577e4e2ecbc2d", "15a577e4e2ecbc2e", "15a577e68c6cbc2f"]);
        responses.Take(4).ShouldAllBe(static response => response.Root.GetProperty("cancelled").GetBoolean());
        responses[4].Root.TryGetProperty("answers", out _).ShouldBeTrue();

        // notify, setStatus and setWidget were answered with nothing; the run went on to its normal result.
        process.Written.Count.ShouldBe(3 + responses.Count);
        AssistantText(messages).ShouldContain("select=undefined confirm=false input=undefined editor=undefined");
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Fact]
    public async Task Omps_cancel_frame_closes_the_open_question_without_a_response()
    {
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("ask-two-questions"));
        process.OnWrite = (omp, command) =>
        {
            switch (command.Type)
            {
                case "set_ask_dialog":
                    omp.Emit(scenario.AskDialogReply);
                    break;
                case "get_state":
                    omp.Emit(scenario.StateReply);
                    break;
                case "prompt":
                    omp.Emit(scenario.UntilDialog);
                    break;
            }
        };
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, token) =>
            {
                token.Register(() => handlerCancelled.TrySetResult());
                handlerStarted.TrySetResult();
                return new TaskCompletionSource<OmpUserInputResponse?>().Task;
            }),
            "Ask me");

        await handlerStarted.Task.WaitAsync(TestTimeout);

        // OMP closes the question itself (a session transition), then ends the run as aborted.
        process.Emit($$"""{"type":"extension_ui_request","id":"cancel-1","method":"cancel","targetId":"{{TwoQuestionsDialogId}}"}""");
        await handlerCancelled.Task.WaitAsync(TestTimeout);
        process.Emit(Fixture("ask-abort").SkipWhile(static line => !line.Contains("\"method\":\"cancel\"", StringComparison.Ordinal)).Skip(1)
            .Where(static line => !line.Contains("\"command\":\"abort\"", StringComparison.Ordinal)));
        var messages = await run;

        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldBe(["set_ask_dialog", "get_state", "prompt"]);

        // Nothing the host sent ended the run, so the abort is a failure.
        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("text").GetString().ShouldBe("OMP aborted the run.");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("aborted");
    }

    public enum Outcome
    {
        HandlerThrows,
        NullAnswer,
        MissingAnswer,
        EmptyAnswer,
        Timeout
    }

    [Theory]
    [InlineData(Outcome.HandlerThrows)]
    [InlineData(Outcome.NullAnswer)]
    [InlineData(Outcome.MissingAnswer)]
    [InlineData(Outcome.EmptyAnswer)]
    [InlineData(Outcome.Timeout)]
    public async Task A_request_without_a_full_answer_is_cancelled_exactly_once_and_the_aborted_run_ends_normally(Outcome outcome)
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-cancelled")).Attach(process);
        CancellationToken handlerToken = default;
        var options = RpcOptions(
            (_, token) =>
            {
                handlerToken = token;
                return outcome switch
                {
                    Outcome.HandlerThrows => throw new InvalidOperationException("handler failed"),
                    Outcome.NullAnswer => Task.FromResult<OmpUserInputResponse?>(null),
                    Outcome.MissingAnswer => Task.FromResult<OmpUserInputResponse?>(Answers((ColourQuestion, ["Blue"]))),
                    Outcome.EmptyAnswer => Task.FromResult<OmpUserInputResponse?>(Answers((ColourQuestion, ["Blue"]), (FeaturesQuestion, []))),
                    _ => new TaskCompletionSource<OmpUserInputResponse?>().Task
                };
            },
            outcome == Outcome.Timeout ? TimeSpan.FromMilliseconds(150) : null);

        var messages = await CollectAsync(new ScriptedProvider(process), options, "Ask me");

        // OMP never sees a partial answer list, and never the timed-out marker (it would pick options for the user).
        var line = process.Written.Single(static written => written.Contains("extension_ui_response", StringComparison.Ordinal));
        line.ShouldBe("""{"type":"extension_ui_response","id":"15a577110e6a2562","cancelled":true}""");

        // OMP then ends the run as aborted ("Ask tool was cancelled by the user"): a normal end, not a failure.
        messages.Single(static message => message.Type == "tool.failed").Content.GetProperty("text").GetString()
            .ShouldBe("Ask tool was cancelled by the user");
        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.completed");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("aborted");
        terminal.Content.GetProperty("session_id").GetString().ShouldBe("01a138a6-ec97-7424-9033-251d0da00520");
        messages.ShouldNotContain(static message => message.Type == "terminal.failed");
        if (outcome == Outcome.Timeout)
        {
            handlerToken.IsCancellationRequested.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task An_ask_request_that_repeats_an_open_request_id_is_ignored()
    {
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("ask-two-questions"));
        scenario = scenario with { UntilDialog = [.. scenario.UntilDialog, scenario.UntilDialog[^1]] };
        scenario.Attach(process);
        var handlerCalls = 0;
        var gate = new TaskCompletionSource<OmpUserInputResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return gate.Task;
            }),
            "Ask me");
        await WaitUntilAsync(() => handlerCalls > 0 && process.ReaderWaiting);
        gate.SetResult(Answers((ColourQuestion, ["Blue"]), (FeaturesQuestion, ["Dark theme"])));
        var messages = await run;

        handlerCalls.ShouldBe(1);
        messages.Count(static message => message.Type == OmpUserInputRequest.MessageType).ShouldBe(1);
        process.Written.Select(ScriptedOmpProcess.Parse).Count(static command => command.Type == "extension_ui_response").ShouldBe(1);
    }

    [Fact]
    public async Task A_large_question_payload_reaches_the_handler_whole()
    {
        var description = string.Concat(Enumerable.Repeat("界x\u2028é", 47_500));
        var frame = JsonSerializer.Serialize(new
        {
            type = "extension_ui_request",
            id = "big-1",
            method = "ask",
            questions = new[]
            {
                new { id = "big", question = "Big?", header = "Big", options = new[] { new { label = "A", description }, new { label = "B", description = "small" } } }
            }
        });
        Encoding.UTF8.GetByteCount(frame).ShouldBeGreaterThan(850_000);
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("ask-cancelled"));
        (scenario with { UntilDialog = [.. scenario.UntilDialog.SkipLast(1), frame] }).Attach(process);
        var requests = new List<OmpUserInputRequest>();

        await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<OmpUserInputResponse?>(Answers(("Big?", ["A"])));
            }),
            "Ask me");

        var option = requests.ShouldHaveSingleItem().Questions.ShouldHaveSingleItem().Options[0];
        option.Description.ShouldBe(description);
        process.Written.Single(static line => line.Contains("extension_ui_response", StringComparison.Ordinal))
            .ShouldBe("""{"type":"extension_ui_response","id":"big-1","answers":[{"id":"big","selectedOptions":["A"]}]}""");
    }

    [Fact]
    public void TryFromCliMessage_reads_only_ask_requests()
    {
        var ask = Fixture("ask-two-questions").Single(static line => line.Contains("\"method\":\"ask\"", StringComparison.Ordinal));
        var select = """{"type":"extension_ui_request","id":"s-1","method":"select","title":"Pick one","options":["a","b"]}""";
        var noQuestionId = """{"type":"extension_ui_request","id":"a-1","method":"ask","questions":[{"question":"Q?","options":[{"label":"A"}]}]}""";
        var noQuestions = """{"type":"extension_ui_request","id":"a-1","method":"ask","questions":[]}""";

        OmpUserInputRequest.TryFromCliMessage(Message(OmpUserInputRequest.MessageType, ask), out var request).ShouldBeTrue();
        request.Questions[0].Options[1].ShouldBe(new OmpUserQuestionOption("Blue", "Cool"));
        OmpUserInputRequest.TryFromCliMessage(Message("assistant", ask), out _).ShouldBeFalse();
        OmpUserInputRequest.TryFromCliMessage(Message(OmpUserInputRequest.MessageType, select), out _).ShouldBeFalse();
        OmpUserInputRequest.TryFromCliMessage(Message(OmpUserInputRequest.MessageType, noQuestionId), out _).ShouldBeFalse();
        OmpUserInputRequest.TryFromCliMessage(Message(OmpUserInputRequest.MessageType, noQuestions), out _).ShouldBeFalse();
    }

    [Fact]
    public async Task An_ask_request_that_cannot_be_read_is_declined_so_omp_ends_the_run_normally()
    {
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("ask-cancelled"));
        (scenario with { UntilDialog = [.. scenario.UntilDialog.SkipLast(1), """{"type":"extension_ui_request","id":"15a577110e6a2562","method":"ask","questions":"oops"}"""] })
            .Attach(process);
        var handlerCalls = 0;

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<OmpUserInputResponse?>(null);
            }),
            "Ask me");

        handlerCalls.ShouldBe(0);
        messages.ShouldNotContain(static message => message.Type == OmpUserInputRequest.MessageType);
        process.Written.Single(static line => line.Contains("extension_ui_response", StringComparison.Ordinal))
            .ShouldBe("""{"type":"extension_ui_response","id":"15a577110e6a2562","cancelled":true}""");
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    // ---------------------------------------------------------------- answer conversion (2.2)

    private static readonly OmpUserQuestion SingleSelect = new(
        "colour", ColourQuestion, "Colour", false, [new OmpUserQuestionOption("Red", "Warm"), new OmpUserQuestionOption("Blue", "Cool")]);

    private static readonly OmpUserQuestion MultiSelect = new(
        "features",
        FeaturesQuestion,
        "Features",
        true,
        [new OmpUserQuestionOption("Dark theme", ""), new OmpUserQuestionOption("Mobile layout", ""), new OmpUserQuestionOption("Offline mode", "")]);

    [Theory]
    [InlineData("Blue", new[] { "Blue" }, null)]
    [InlineData("blue", new string[0], "blue")]
    [InlineData("Teal", new string[0], "Teal")]
    [InlineData("Red, Blue", new string[0], "Red, Blue")]
    [InlineData(" Blue", new string[0], " Blue")]
    public void A_single_select_answer_selects_exactly_one_offered_label_or_is_free_text(string text, string[] selected, string? customInput)
    {
        var answer = OmpAskAnswer.From(SingleSelect, text);

        answer.Id.ShouldBe("colour");
        answer.SelectedOptions.ShouldBe(selected);
        answer.CustomInput.ShouldBe(customInput);
    }

    [Theory]
    [InlineData("Dark theme", new[] { "Dark theme" }, null)]
    [InlineData("Offline mode, Dark theme", new[] { "Offline mode", "Dark theme" }, null)]
    [InlineData("Dark theme, Mobile layout, Offline mode", new[] { "Dark theme", "Mobile layout", "Offline mode" }, null)]
    [InlineData("Dark theme, Dark theme", new string[0], "Dark theme, Dark theme")]
    [InlineData("Dark theme, Purple", new string[0], "Dark theme, Purple")]
    [InlineData("Dark theme,Mobile layout", new string[0], "Dark theme,Mobile layout")]
    [InlineData("Dark theme, ", new string[0], "Dark theme, ")]
    [InlineData("Something else", new string[0], "Something else")]
    public void A_multi_select_answer_selects_distinct_offered_labels_or_is_free_text(string text, string[] selected, string? customInput)
    {
        var answer = OmpAskAnswer.From(MultiSelect, text);

        answer.Id.ShouldBe("features");
        answer.SelectedOptions.ShouldBe(selected);
        answer.CustomInput.ShouldBe(customInput);
    }

    [Theory]
    [InlineData("Red, dark", new[] { "Red, dark" })]
    [InlineData("Red, dark, Blue", new[] { "Red, dark", "Blue" })]
    [InlineData("Blue, Red, dark", new[] { "Blue", "Red, dark" })]
    [InlineData("Red", new[] { "Red" })]
    public void A_label_that_contains_a_comma_is_matched_longest_first(string text, string[] selected)
    {
        var question = new OmpUserQuestion(
            "q", "Which?", "", true, [new OmpUserQuestionOption("Red", ""), new OmpUserQuestionOption("Red, dark", ""), new OmpUserQuestionOption("Blue", "")]);

        OmpAskAnswer.From(question, text).SelectedOptions.ShouldBe(selected);
        OmpAskAnswer.From(question, text).CustomInput.ShouldBeNull();
    }

    [Fact]
    public void The_longest_label_first_split_backtracks_when_the_longest_match_leads_nowhere()
    {
        var question = new OmpUserQuestion(
            "q", "Which?", "", true, [new OmpUserQuestionOption("A, B", ""), new OmpUserQuestionOption("A", ""), new OmpUserQuestionOption("B, C", "")]);

        OmpAskAnswer.From(question, "A, B, C").SelectedOptions.ShouldBe(["A", "B, C"]);
        OmpAskAnswer.From(question, "A, B").SelectedOptions.ShouldBe(["A, B"]);
    }

    [Fact]
    public void A_single_select_label_that_contains_a_comma_is_one_option()
    {
        var question = new OmpUserQuestion("q", "Which?", "", false, [new OmpUserQuestionOption("Yes, please", ""), new OmpUserQuestionOption("No", "")]);

        OmpAskAnswer.From(question, "Yes, please").SelectedOptions.ShouldBe(["Yes, please"]);
    }

    [Fact]
    public void The_answer_list_follows_the_request_order_and_joins_the_values_of_a_question()
    {
        var request = new OmpUserInputRequest("r-1", [SingleSelect, MultiSelect]);
        var response = Answers((FeaturesQuestion, ["Dark theme", "Offline mode"]), (ColourQuestion, ["Red"]));

        var answers = response.ToAskAnswers(request);

        answers.ShouldNotBeNull();
        answers.Select(static answer => answer.Id).ShouldBe(["colour", "features"]);
        answers[0].SelectedOptions.ShouldBe(["Red"]);
        answers[1].SelectedOptions.ShouldBe(["Dark theme", "Offline mode"]);
        OmpRpcProtocol.BuildAskAnswers(request.RequestId, answers).ShouldBe(
            """{"type":"extension_ui_response","id":"r-1","answers":[{"id":"colour","selectedOptions":["Red"]},{"id":"features","selectedOptions":["Dark theme","Offline mode"]}]}""");
    }

    [Fact]
    public void A_response_that_misses_a_question_or_answers_it_with_nothing_is_not_converted()
    {
        var request = new OmpUserInputRequest("r-1", [SingleSelect, MultiSelect]);

        Answers((ColourQuestion, ["Red"])).ToAskAnswers(request).ShouldBeNull();
        Answers((ColourQuestion, ["Red"]), (FeaturesQuestion, [])).ToAskAnswers(request).ShouldBeNull();
        Answers((ColourQuestion, ["  "]), (FeaturesQuestion, ["Dark theme"])).ToAskAnswers(request).ShouldBeNull();
    }

    [Fact]
    public void The_answers_omp_rejected_in_the_capture_are_never_produced_for_the_same_input()
    {
        // Each capture line holds the ask request, the answer frame the probe sent and the tool error OMP returned for it.
        foreach (var line in Fixture("ask-rejected-answers"))
        {
            var record = JsonDocument.Parse(line).RootElement;
            OmpUserInputRequest.TryParse(record.GetProperty("request"), out var request).ShouldBeTrue();
            record.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
            var error = record.GetProperty("result").GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
            error.ShouldStartWith("Ask dialog answer");

            // The same choices handed to the provider as neutral answer values.
            var neutral = new Dictionary<string, IReadOnlyList<string>>();
            foreach (var sent in record.GetProperty("sent").GetProperty("answers").EnumerateArray())
            {
                var question = request.Questions.Single(question => question.Id == sent.GetProperty("id").GetString());
                var values = sent.GetProperty("selectedOptions").EnumerateArray().Select(static value => value.GetString()!).ToList();
                if (sent.TryGetProperty("customInput", out var customInput))
                {
                    values.Add(customInput.GetString()!);
                }

                neutral[question.Question] = values.Count == 0 ? ["Dark theme"] : values;
            }

            var answers = new OmpUserInputResponse(neutral).ToAskAnswers(request);

            answers.ShouldNotBeNull();
            foreach (var (question, answer) in request.Questions.Zip(answers))
            {
                var labels = question.Options.Select(static option => option.Label).ToArray();
                answer.SelectedOptions.ShouldAllBe(label => labels.Contains(label));
                answer.SelectedOptions.Distinct().Count().ShouldBe(answer.SelectedOptions.Count);
                if (!question.MultiSelect)
                {
                    answer.SelectedOptions.Count.ShouldBeLessThanOrEqualTo(1);
                    (answer.SelectedOptions.Count == 1 && answer.CustomInput is not null).ShouldBeFalse();
                }
            }
        }
    }

    // ---------------------------------------------------------------- aborted outcomes and cancellation (3.5)

    [Fact]
    public async Task An_aborted_run_the_host_did_not_ask_for_is_a_failure()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("abort-while-streaming")).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Write a story");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("text").GetString().ShouldBe("OMP aborted the run.");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("aborted");
        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldBe(["set_ask_dialog", "get_state", "prompt"]);
    }

    [Fact]
    public async Task Cancelling_while_a_question_is_open_sends_only_abort_and_ends_the_process()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-abort")).Attach(process, emitAfterOn: "abort");
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken = default;
        using var cancellation = new CancellationTokenSource();
        var received = Channel.CreateUnbounded<CliMessage>();
        var provider = new ScriptedProvider(process);
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               RpcOptions((_, token) =>
                               {
                                   handlerToken = token;
                                   handlerStarted.TrySetResult();
                                   return new TaskCompletionSource<OmpUserInputResponse?>().Task;
                               }),
                               "Ask me",
                               cancellation.Token))
            {
                await received.Writer.WriteAsync(message);
            }
        });

        await handlerStarted.Task.WaitAsync(TestTimeout);
        await ReadUntilAsync(received.Reader, static message => message.Type == OmpUserInputRequest.MessageType);
        await cancellation.CancelAsync();

        await AssertCanceledAsync(run);

        // No answer was written for the open question: OMP closed it with its cancel frame, settled, and exited on the closed
        // input.
        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldBe(["set_ask_dialog", "get_state", "prompt", "abort"]);
        handlerToken.IsCancellationRequested.ShouldBeTrue();
        process.Log.IndexOf("close-input").ShouldBeGreaterThan(process.Log.IndexOf("emit:session_settled"));
        process.InputClosed.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
        process.Stopped.ShouldBeFalse();
        process.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Cancelling_a_run_that_has_no_open_question_aborts_it_and_ends_the_process()
    {
        var process = new ScriptedOmpProcess();
        var scenario = Scenario.From(Fixture("abort-while-streaming"));
        var firstAssistant = scenario.UntilDialog.ToList().FindIndex(static line => line.Contains("\"text_delta\"", StringComparison.Ordinal));
        (scenario with { UntilDialog = scenario.UntilDialog.Take(firstAssistant + 1).ToArray(), AfterDialog = scenario.UntilDialog.Skip(firstAssistant + 1).ToArray() })
            .Attach(process, emitAfterOn: "abort");
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider(process);
        var received = Channel.CreateUnbounded<CliMessage>();
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(RpcOptions(NoAnswer), "Write a story", cancellation.Token))
            {
                await received.Writer.WriteAsync(message);
            }
        });

        await ReadUntilAsync(received.Reader, static message => message.Type == "assistant");
        await cancellation.CancelAsync();

        await AssertCanceledAsync(run);
        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldBe(["set_ask_dialog", "get_state", "prompt", "abort"]);
        process.InputClosed.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
        process.Stopped.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_stops_a_process_that_neither_settles_nor_exits_and_still_surfaces_the_cancellation()
    {
        var process = new ScriptedOmpProcess { ExitOnInputClose = false };
        var scenario = Scenario.From(Fixture("ask-two-questions"));
        process.OnWrite = (omp, command) =>
        {
            if (command.Type == "set_ask_dialog")
            {
                omp.Emit(scenario.AskDialogReply);
            }
            else if (command.Type == "get_state")
            {
                omp.Emit(scenario.StateReply);
            }
        };
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider(process) { RpcExitTimeout = TimeSpan.FromMilliseconds(200) };
        var received = Channel.CreateUnbounded<CliMessage>();
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(RpcOptions(NoAnswer), "Ask me", cancellation.Token))
            {
                await received.Writer.WriteAsync(message);
            }
        });

        await ReadUntilAsync(received.Reader, static message => message.Type == "session.started");
        await WaitUntilAsync(() => process.Written.Count >= 3);
        await cancellation.CancelAsync();

        await AssertCanceledAsync(run);
        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldBe(["set_ask_dialog", "get_state", "prompt", "abort"]);
        process.Stopped.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
    }

    [Fact]
    public async Task A_consumer_that_stops_enumerating_aborts_the_run_and_ends_the_process()
    {
        var process = new ScriptedOmpProcess();
        Scenario.From(Fixture("ask-abort")).Attach(process, emitAfterOn: "abort");
        var provider = new ScriptedProvider(process);

        await Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(RpcOptions(NoAnswer), "Ask me"))
            {
                message.Type.ShouldBe("session.started");
                break;
            }
        }).WaitAsync(TestTimeout);

        process.Written.Select(ScriptedOmpProcess.Parse).Select(static command => command.Type).ShouldContain("abort");
        process.InputClosed.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
        process.Disposed.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- a real process (the default IOmpRpcProcess)

    [Fact]
    public async Task ExecuteAsync_runs_the_rpc_conversation_over_a_real_process()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var scenario = Scenario.From(Fixture("ask-two-questions"));
        using var directory = new ScratchDirectory();
        string[] parts = ["startup", "ask-reply", "state-reply", "until-dialog", "after-dialog"];
        IReadOnlyList<string>[] contents = [scenario.Startup, [scenario.AskDialogReply], [scenario.StateReply], scenario.UntilDialog, scenario.AfterDialog];
        var paths = new List<string>();
        for (var index = 0; index < parts.Length; index++)
        {
            var path = Path.Combine(directory.Path, parts[index] + ".jsonl");
            await File.WriteAllLinesAsync(path, contents[index]);
            paths.Add(path);
        }

        var receivedPath = Path.Combine(directory.Path, "received.txt");

        // The script plays omp: it answers each command it reads, records what it was sent, and exits only when its standard
        // input ends, as omp does. Absolute tool paths only: other tests leave PATH pointing at a stub directory.
        var script = directory.WriteScript(
            "fake-omp.sh",
            """
            /bin/cat "$1"
            IFS= read -r set_ask
            /bin/cat "$2"
            IFS= read -r get_state
            /bin/cat "$3"
            IFS= read -r prompt
            /bin/cat "$4"
            IFS= read -r response
            printf '%s\n' "$set_ask" "$get_state" "$prompt" "$response" > "$6"
            /bin/cat "$5"
            /bin/cat >/dev/null
            exit 0
            """);
        var manager = new ScriptProcessManager(script, [.. paths, receivedPath]);
        var provider = new OmpProvider(new StubExecutableResolver(), manager);

        var messages = await CollectAsync(provider, RpcOptions(AnswerAll("Blue", "Dark theme, Offline mode")), "Ask me");

        messages.Last().Type.ShouldBe("terminal.completed");
        messages.ShouldContain(static message => message.Type == OmpUserInputRequest.MessageType);
        var received = (await File.ReadAllLinesAsync(receivedPath)).Select(ScriptedOmpProcess.Parse).ToList();
        received.Select(static command => command.Type).ShouldBe(["set_ask_dialog", "get_state", "prompt", "extension_ui_response"]);
        received[2].Root.GetProperty("message").GetString().ShouldBe("Ask me");
        received[3].Id.ShouldBe(TwoQuestionsDialogId);
        manager.LastContext!.Arguments.Take(2).ShouldBe(["--mode", "rpc-ui"]);
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyList<string> Fixture(string name)
    {
        return File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Omp", name + ".jsonl"))
            .Where(static line => line.Length > 0)
            .ToArray();
    }

    private static CliMessage Message(string type, string json) => new(type, JsonDocument.Parse(json).RootElement.Clone());

    /// <summary>
    /// A captured rpc-ui line as the mapper must see it: message_update without the top-level message snapshot, and text and
    /// thinking events without their partial snapshot (as --mode json sends them); tool-call events keep theirs.
    /// </summary>
    private static string WithoutStaleSnapshot(string line)
    {
        if (System.Text.Json.Nodes.JsonNode.Parse(line) is not System.Text.Json.Nodes.JsonObject frame
            || frame["type"]?.GetValue<string>() != "message_update")
        {
            return line;
        }

        frame.Remove("message");
        frame.Remove("messageId");
        if (frame["assistantMessageEvent"] is System.Text.Json.Nodes.JsonObject assistantEvent
            && assistantEvent["type"]?.GetValue<string>()?.StartsWith("toolcall_", StringComparison.Ordinal) != true)
        {
            assistantEvent.Remove("partial");
        }

        return frame.ToJsonString();
    }

    private static string AssistantText(IEnumerable<CliMessage> messages)
    {
        return string.Concat(messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.GetProperty("text").GetString()));
    }

    private static OmpOptions RpcOptions(
        Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> handler,
        TimeSpan? questionTimeout = null)
    {
        return new OmpOptions
        {
            Model = "mock-model",
            NoSession = true,
            UserInputHandler = handler,
            QuestionTimeout = questionTimeout ?? OmpOptions.DefaultQuestionTimeout
        };
    }

    /// <summary>Answers the questions of a request in order, one value each.</summary>
    private static Func<OmpUserInputRequest, CancellationToken, Task<OmpUserInputResponse?>> AnswerAll(params string[] values)
    {
        return (request, _) => Task.FromResult<OmpUserInputResponse?>(new OmpUserInputResponse(
            request.Questions.Zip(values).ToDictionary(static pair => pair.First.Question, static pair => (IReadOnlyList<string>)[pair.Second])));
    }

    private static OmpUserInputResponse Answers(params (string Question, string[] Values)[] answers)
    {
        return new OmpUserInputResponse(
            answers.ToDictionary(static pair => pair.Question, static pair => (IReadOnlyList<string>)pair.Values));
    }

    private static async Task<List<CliMessage>> CollectAsync(
        OmpProvider provider,
        OmpOptions options,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var messages = new List<CliMessage>();
        await Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(options, prompt, cancellationToken))
            {
                messages.Add(message);
            }
        }, CancellationToken.None).WaitAsync(TestTimeout);
        return messages;
    }

    private static async Task AssertCanceledAsync(Task run)
    {
        var exception = await Record.ExceptionAsync(() => run.WaitAsync(TestTimeout));
        exception.ShouldBeAssignableTo<OperationCanceledException>();
    }

    private static async Task<CliMessage> ReadUntilAsync(ChannelReader<CliMessage> reader, Func<CliMessage, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (true)
        {
            var message = await reader.ReadAsync(timeout.Token);
            if (predicate(message))
            {
                return message;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true in time.");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The conversation of a capture, cut at the replies to the host's commands: the startup frames, the reply to
    /// set_ask_dialog, the reply to get_state, the prompt reply with the run up to the first ask request, and the rest.
    /// </summary>
    private sealed record Scenario(
        IReadOnlyList<string> Startup,
        string AskDialogReply,
        string StateReply,
        IReadOnlyList<string> UntilDialog,
        IReadOnlyList<string> AfterDialog)
    {
        public static Scenario From(IReadOnlyList<string> capture)
        {
            var lines = capture.ToList();
            var askReply = lines.FindIndex(static line => IsResponse(line, "set_ask_dialog"));
            var stateReply = lines.FindIndex(static line => IsResponse(line, "get_state"));
            var startup = lines.Take(askReply).ToArray();
            if (stateReply < 0)
            {
                return new Scenario(startup, lines[askReply], string.Empty, lines.Skip(askReply + 1).ToArray(), []);
            }

            var run = lines.Skip(stateReply + 1).ToList();
            var dialog = run.FindIndex(static line => line.Contains("\"method\":\"ask\"", StringComparison.Ordinal));
            return dialog < 0
                ? new Scenario(startup, lines[askReply], lines[stateReply], run, [])
                : new Scenario(startup, lines[askReply], lines[stateReply], run.Take(dialog + 1).ToArray(), run.Skip(dialog + 1).ToArray());
        }

        public static string SessionIdOf(IReadOnlyList<string> capture)
        {
            var reply = JsonDocument.Parse(capture.First(static line => IsResponse(line, "get_state"))).RootElement;
            return reply.GetProperty("data").GetProperty("sessionId").GetString()!;
        }

        /// <summary>
        /// Makes the process play the capture: the startup frames when it starts, each reply when the host writes its command,
        /// and the rest of the run on the host's answer (or on <paramref name="emitAfterOn" />).
        /// </summary>
        public void Attach(ScriptedOmpProcess process, string emitAfterOn = "extension_ui_response")
        {
            var emittedAfter = 0;
            process.OnStart = omp => omp.Emit(Startup);
            process.OnWrite = (omp, command) =>
            {
                if (command.Type == "set_ask_dialog")
                {
                    omp.Emit(AskDialogReply);
                }
                else if (command.Type == "get_state")
                {
                    omp.Emit(StateReply);
                }
                else if (command.Type == "prompt")
                {
                    omp.Emit(UntilDialog);
                }
                else if (command.Type == emitAfterOn && Interlocked.Exchange(ref emittedAfter, 1) == 0)
                {
                    omp.Emit(AfterDialog);
                }
            };
        }

        private static bool IsResponse(string line, string command)
        {
            var root = JsonDocument.Parse(line).RootElement;
            return root.TryGetProperty("type", out var type) && type.GetString() == "response"
                   && root.TryGetProperty("command", out var name) && name.GetString() == command;
        }
    }

    /// <summary>
    /// An omp process played by the test: lines queued with <see cref="Emit(string)" /> are its standard output, every line the
    /// host writes is recorded and handed to <see cref="OnWrite" />, and closing standard input ends it like omp does.
    /// <see cref="Log" /> records writes, emitted frame types and the closing of the input in the order they happened.
    /// </summary>
    private sealed class ScriptedOmpProcess : IOmpRpcProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _standardError = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _written = [];
        private readonly List<string> _log = [];
        private readonly List<string> _held = [];
        private readonly object _gate = new();
        private bool _holding;
        private volatile bool _readerWaiting;

        /// <summary>Called, on the writing thread, for every line the host writes.</summary>
        public Action<ScriptedOmpProcess, Command>? OnWrite { get; set; }

        /// <summary>Called once, when the provider starts to use the process.</summary>
        public Action<ScriptedOmpProcess>? OnStart { get; set; }

        /// <summary>When it matches a command, what <see cref="OnWrite" /> emits for it is held until <see cref="ReleaseHeldReplies" />.</summary>
        public Func<Command, bool>? HoldReply { get; set; }

        /// <summary>Whether closing standard input makes the process exit with code 0, as omp does.</summary>
        public bool ExitOnInputClose { get; init; } = true;

        public bool InputClosed { get; private set; }

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public bool HasExited => _exit.Task.IsCompleted;

        /// <summary>Whether the reader found no line waiting and is blocked until one is emitted.</summary>
        public bool ReaderWaiting => _readerWaiting;

        public IReadOnlyList<string> Written
        {
            get
            {
                lock (_gate)
                {
                    return [.. _written];
                }
            }
        }

        public List<string> Log
        {
            get
            {
                lock (_gate)
                {
                    return [.. _log];
                }
            }
        }

        public Task<string> StandardError => _standardError.Task;

        public static Command Parse(string line)
        {
            var root = JsonDocument.Parse(line).RootElement.Clone();
            return new Command(
                root.TryGetProperty("type", out var type) ? type.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                root);
        }

        public void Emit(string line)
        {
            lock (_gate)
            {
                if (_holding)
                {
                    _held.Add(line);
                    return;
                }

                if (HasExited)
                {
                    return;
                }

                _log.Add(DescribeEmitted(line));
                _output.Writer.TryWrite(line);
            }
        }

        public void Emit(IEnumerable<string> lines)
        {
            foreach (var line in lines.ToArray())
            {
                Emit(line);
            }
        }

        public void ReleaseHeldReplies()
        {
            string[] held;
            lock (_gate)
            {
                _holding = false;
                held = [.. _held];
                _held.Clear();
            }

            Emit(held);
        }

        public void Exit(int exitCode, string standardError = "")
        {
            _output.Writer.TryComplete();
            _standardError.TrySetResult(standardError);
            _exit.TrySetResult(exitCode);
        }

        public void Start() => OnStart?.Invoke(this);

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (_output.Reader.TryRead(out var line))
            {
                return line;
            }

            _readerWaiting = true;
            try
            {
                return await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
            finally
            {
                _readerWaiting = false;
            }
        }

        private static string DescribeEmitted(string line)
        {
            try
            {
                var command = Parse(line);
                return command.Type == "response" ? $"emit:response:{command.Root.GetProperty("command").GetString()}" : $"emit:{command.Type}";
            }
            catch (JsonException)
            {
                return "emit:raw";
            }
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited || InputClosed)
            {
                throw new IOException("Broken pipe");
            }

            var command = Parse(line);
            lock (_gate)
            {
                _written.Add(line);
                _log.Add($"write:{command.Type}");
                _holding = HoldReply?.Invoke(command) == true;
            }

            OnWrite?.Invoke(this, command);
            return ValueTask.CompletedTask;
        }

        public void CloseInput()
        {
            lock (_gate)
            {
                _log.Add("close-input");
            }

            InputClosed = true;
            if (ExitOnInputClose)
            {
                Exit(0);
            }
        }

        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public Task StopAsync()
        {
            Stopped = true;
            Exit(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public sealed record Command(string Type, string? Id, JsonElement Root);
    }

    /// <summary>
    /// A provider whose RPC process is the scripted one, whose print path is a canned completion and whose version lookup
    /// starts no process.
    /// </summary>
    private sealed class ScriptedProvider : OmpProvider
    {
        private readonly ScriptedOmpProcess _process;

        public ScriptedProvider(ScriptedOmpProcess process, ILogger<OmpProvider>? logger = null)
            : base(new StubExecutableResolver(), new CliProcessManager(), null, logger)
        {
            _process = process;
        }

        public ProcessStartContext? RpcContext { get; private set; }

        public ProcessStartContext? PrintContext { get; private set; }

        public int RpcStarts { get; private set; }

        internal override ValueTask<IOmpRpcProcess> StartRpcProcessAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
        {
            RpcContext = startContext;
            RpcStarts++;
            _process.Start();
            return ValueTask.FromResult<IOmpRpcProcess>(_process);
        }

        internal override async IAsyncEnumerable<CliMessage> ExecuteProcessAsync(
            ProcessStartContext startContext,
            string? requestedSessionId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            PrintContext = startContext;
            await Task.CompletedTask;
            yield return new CliMessage(
                "terminal.completed",
                JsonSerializer.SerializeToElement(new { type = "terminal.completed", text = "print path" }));
        }

        internal override Task<string?> ReadVersionAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
            => Task.FromResult<string?>("omp/0.0.0-test");
    }

    /// <summary>Starts a shell script instead of the requested executable and records the request.</summary>
    private sealed class ScriptProcessManager(string scriptPath, IReadOnlyList<string> scriptArguments) : CliProcessManager
    {
        public ProcessStartContext? LastContext { get; private set; }

        public int Starts { get; private set; }

        public override ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            Starts++;
            return base.StartAsync(
                new ProcessStartContext
                {
                    ExecutablePath = scriptPath,
                    Arguments = scriptArguments,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath),
                    InputEncoding = context.InputEncoding,
                    OutputEncoding = context.OutputEncoding
                },
                cancellationToken);
        }
    }

    private sealed class StubExecutableResolver : CliExecutableResolver
    {
        public override string? ResolveExecutablePath(string? executableName, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableName;

        public override string? ResolveFirstAvailablePath(IEnumerable<string> executableNames, IReadOnlyDictionary<string, string?>? environmentVariables = null)
            => executableNames.FirstOrDefault();
    }

    private sealed class CapturingLogger : ILogger<OmpProvider>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-omp-question-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteScript(string name, string body)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
