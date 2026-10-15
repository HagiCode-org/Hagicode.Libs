using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Pi;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

// Interactive questions on Pi: the RPC path that a PiOptions.UserInputHandler switches on. The conversation is scripted:
// PiRpcCapture holds real pi 1.2.0 output, the scripted process plays pi (it answers get_state and prompt the way the capture
// did and reacts to the host's writes), so none of these tests needs pi, credentials or a model.
public sealed class PiProviderUserQuestionTests
{
    private const string SingleQuestionText = "Which colour do you prefer: Red or Blue?";

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private static readonly Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>> NoAnswer =
        static (_, _) => Task.FromResult<PiUserInputResponse?>(null);

    // ---------------------------------------------------------------- default path regression guard (3.1)

    [Test]
    public async Task ExecuteAsync_without_a_handler_keeps_the_print_command_closes_stdin_and_completes_on_exit()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new ScratchDirectory();
        var stdoutPath = Path.Combine(directory.Path, "stdout.jsonl");
        await File.WriteAllLinesAsync(stdoutPath,
        [
            """{"type":"session","version":3,"id":"pi-session-1","timestamp":"2026-06-07T09:39:20.000Z","cwd":"/tmp/pi-project"}""",
            """{"type":"turn_end","message":{"role":"assistant","content":[{"type":"text","text":"hi"}],"provider":"mock","model":"mock-model","stopReason":"stop"},"toolResults":[]}""",
            """{"type":"agent_end","messages":[],"willRetry":false}"""
        ]);

        // The script prints only after its standard input ended, so a provider that left stdin open would hang here.
        var manager = new ScriptProcessManager(
            directory.WriteScript("print.sh", "cat >/dev/null\ncat \"$1\"\nexit 0\n"),
            [stdoutPath]);
        var provider = new PiProvider(new StubExecutableResolver(), manager);

        var messages = await CollectAsync(provider, new PiOptions { Model = "mock-model", NoSession = true }, "Say hi");

        manager.LastContext.ShouldNotBeNull();
        manager.LastContext.Arguments.ShouldBe(["--mode", "json", "--print", "--model", "mock-model", "--no-session", "Say hi"]);
        manager.Starts.ShouldBe(1);
        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.completed"]);
    }

    [Test]
    public async Task ExecuteAsync_with_every_tool_disabled_ignores_the_handler_and_loads_no_extension()
    {
        var provider = new ScriptedProvider(new ScriptedPiProcess());
        var options = new PiOptions
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

    // ---------------------------------------------------------------- RPC command line (3.2)

    [Test]
    public void BuildRpcCommandArguments_selects_rpc_mode_loads_the_extension_and_sends_no_prompt()
    {
        var provider = new ScriptedProvider(new ScriptedPiProcess());

        var arguments = provider.BuildRpcCommandArguments(
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
                ExtraArguments = ["--verbose", "   ", "--offline"],
                UserInputHandler = NoAnswer
            },
            "/ext/ask-user-question.ts");

        arguments.ShouldBe(
        [
            "--mode",
            "rpc",
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
            "read,grep,ask_user_question",
            "--exclude-tools",
            "bash",
            "-e",
            "/ext/ask-user-question.ts",
            "--verbose",
            "--offline"
        ]);
        arguments.ShouldNotContain("--print");
        arguments.ShouldNotContain("json");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BuildRpcCommandArguments_differs_from_the_print_command_only_in_mode_extension_allowlist_and_prompt(bool restrictTools)
    {
        var provider = new ScriptedProvider(new ScriptedPiProcess());
        var options = new PiOptions
        {
            Provider = "anthropic",
            Model = "claude-haiku",
            SystemPrompt = "System",
            AppendSystemPrompts = ["One", "Two"],
            SessionId = "s-1",
            SessionDirectory = "/tmp/sessions",
            Thinking = "high",
            ExcludedTools = ["write"],
            ExtraArguments = ["--offline"],
            AllowedTools = restrictTools ? ["read"] : [],
            UserInputHandler = NoAnswer
        };

        var print = provider.BuildCommandArguments(options, "the prompt").ToList();
        var rpc = provider.BuildRpcCommandArguments(options, "/ext/ask-user-question.ts").ToList();

        // Same options in the same order once the mode flags, the prompt, -e and the question tool are taken out.
        print.RemoveRange(0, 3);
        print.RemoveAt(print.Count - 1);
        rpc.RemoveRange(0, 2);
        var extension = rpc.IndexOf("-e");
        rpc.RemoveRange(extension, 2);
        var tools = rpc.IndexOf("--tools");
        if (restrictTools)
        {
            rpc[tools + 1].ShouldBe("read,ask_user_question");
            rpc[tools + 1] = "read";
        }
        else
        {
            tools.ShouldBe(-1);
        }

        rpc.ShouldBe(print);
    }

    [Test]
    public void BuildRpcCommandArguments_adds_the_question_tool_to_an_allowlist_only_once_and_never_to_an_empty_one()
    {
        var provider = new ScriptedProvider(new ScriptedPiProcess());

        var present = provider.BuildRpcCommandArguments(
            new PiOptions { AllowedTools = [" read ", "ask_user_question"], UserInputHandler = NoAnswer }, "/ext.ts");
        var blank = provider.BuildRpcCommandArguments(
            new PiOptions { AllowedTools = ["  ", ""], UserInputHandler = NoAnswer }, "/ext.ts");

        present[present.ToList().IndexOf("--tools") + 1].ShouldBe("read,ask_user_question");
        blank.ShouldNotContain("--tools");
    }

    [Test]
    public async Task ExecuteAsync_starts_pi_in_rpc_mode_with_the_extracted_extension_and_sends_the_prompt_as_a_command()
    {
        var process = new ScriptedPiProcess();
        var scenario = Scenario.From(PiRpcCapture.SingleQuestion);
        scenario.Attach(process);
        var provider = new ScriptedProvider(process);

        await CollectAsync(
            provider,
            new PiOptions { WorkingDirectory = "/tmp", Model = "mock-model", NoSession = true, UserInputHandler = Answer("Blue") },
            "Ask me about colours");

        provider.RpcContext.ShouldNotBeNull();
        var arguments = provider.RpcContext.Arguments;
        arguments.Take(2).ShouldBe(["--mode", "rpc"]);
        arguments.ShouldNotContain("--print");
        arguments.ShouldNotContain("Ask me about colours");
        var extensionPath = arguments[arguments.ToList().IndexOf("-e") + 1];
        Path.GetFileName(extensionPath).ShouldBe("ask-user-question.ts");
        File.ReadAllText(extensionPath).ShouldContain(PiUserInputRequest.WireMarker);

        var prompt = process.Written.Select(ScriptedPiProcess.Parse).Single(static command => command.Type == "prompt");
        prompt.Root.GetProperty("message").GetString().ShouldBe("Ask me about colours");
        process.Written.Select(ScriptedPiProcess.Parse).Select(static command => command.Type).Take(2).ShouldBe(["get_state", "prompt"]);
    }

    [TestCase(0)]
    [TestCase(-5)]
    public async Task ExecuteAsync_rejects_a_non_positive_question_timeout_before_starting_a_process(int seconds)
    {
        var provider = new ScriptedProvider(new ScriptedPiProcess());
        var options = new PiOptions { UserInputHandler = NoAnswer, QuestionTimeout = TimeSpan.FromSeconds(seconds) };

        var exception = await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await CollectAsync(provider, options, "Ask me"));

        exception.ParamName.ShouldBe("options");
        provider.RpcStarts.ShouldBe(0);
        provider.PrintContext.ShouldBeNull();
    }

    [Test]
    public async Task ExecuteAsync_accepts_an_infinite_question_timeout()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);
        var provider = new ScriptedProvider(process);

        var messages = await CollectAsync(
            provider,
            new PiOptions { UserInputHandler = Answer("Blue"), QuestionTimeout = Timeout.InfiniteTimeSpan },
            "Ask me");

        messages.Last().Type.ShouldBe("terminal.completed");
    }

    [Test]
    public async Task ExecuteAsync_ignores_the_question_timeout_when_there_is_no_handler()
    {
        var provider = new ScriptedProvider(new ScriptedPiProcess());

        var messages = await CollectAsync(provider, new PiOptions { QuestionTimeout = TimeSpan.Zero }, "Say hi");

        provider.RpcStarts.ShouldBe(0);
        messages.ShouldHaveSingleItem().Type.ShouldBe("terminal.completed");
    }

    // ---------------------------------------------------------------- session id and completion (3.3)

    [Test]
    public async Task The_session_id_comes_from_get_state_and_is_reported_as_started()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(Answer("Blue")), "Ask me");

        var session = messages[0];
        session.Type.ShouldBe("session.started");
        session.Content.GetProperty("session_id").GetString().ShouldBe("pi-session-fixture");
        session.Content.GetProperty("sessionId").GetString().ShouldBe("pi-session-fixture");
        session.Content.GetProperty("resumeMode").GetString().ShouldBe("started");
        messages.Count(static message => message.Type.StartsWith("session.", StringComparison.Ordinal)).ShouldBe(1);
    }

    [Test]
    public async Task A_requested_session_that_pi_reports_back_is_reported_as_resumed()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions(Answer("Blue")) with { NoSession = false, SessionId = "pi-session-fixture" },
            "Ask me");

        messages[0].Type.ShouldBe("session.resumed");
        messages[0].Content.GetProperty("requested_session_id").GetString().ShouldBe("pi-session-fixture");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("resumed");
    }

    [Test]
    public async Task A_requested_session_that_pi_does_not_report_back_is_reported_as_restarted()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions(Answer("Blue")) with { NoSession = false, SessionId = "another-session" },
            "Ask me");

        messages[0].Type.ShouldBe("session.started");
        messages[0].Content.GetProperty("resumeMode").GetString().ShouldBe("restarted");
    }

    [Test]
    public async Task The_run_completes_on_agent_settled_then_closes_standard_input_and_waits_for_the_exit()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);
        var provider = new ScriptedProvider(process);

        var messages = await CollectAsync(provider, RpcOptions(Answer("Blue")), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.completed");
        terminal.Content.GetProperty("session_id").GetString().ShouldBe("pi-session-fixture");
        messages.Count(static message => message.Type.StartsWith("terminal.", StringComparison.Ordinal)).ShouldBe(1);
        process.InputClosed.ShouldBeTrue();
        process.Stopped.ShouldBeFalse();
        process.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task A_process_that_does_not_exit_after_the_run_settled_is_stopped_and_the_run_still_completes()
    {
        var process = new ScriptedPiProcess { ExitOnInputClose = false };
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);
        var provider = new ScriptedProvider(process) { RpcExitTimeout = TimeSpan.FromMilliseconds(200) };

        var messages = await CollectAsync(provider, RpcOptions(Answer("Blue")), "Ask me");

        messages.Last().Type.ShouldBe("terminal.completed");
        process.Stopped.ShouldBeTrue();
    }

    [Test]
    public async Task A_process_that_ends_before_the_run_settled_fails_with_its_exit_code_and_standard_error()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
                pi.Exit(3, "pi: out of memory\n");
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        terminal.Content.GetProperty("exit_code").GetInt32().ShouldBe(3);
        var terminalStderr = terminal.Content.GetProperty("stderr").GetString();
        terminalStderr.ShouldNotBeNull();
        terminalStderr.ShouldContain("pi: out of memory");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("pi: out of memory");
    }

    [Test]
    public async Task A_process_that_exits_with_code_zero_before_settling_is_not_reported_as_completed()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
            }
            else if (command.Type == "prompt")
            {
                pi.Emit(PiRpcCapture.ErrorTurn.Take(1));
                pi.Exit(0);
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("before the run settled");
        terminal.Content.GetProperty("session_id").GetString().ShouldBe("pi-session-fixture");
    }

    [Test]
    public async Task A_rejected_prompt_becomes_a_terminal_failure_that_keeps_the_error_text_and_shuts_pi_down()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
            }
            else if (command.Type == "prompt")
            {
                pi.Emit(PiRpcCapture.PromptRejectedNoApiKey);
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        messages.Select(static message => message.Type).ShouldBe(["session.started", "terminal.failed"]);
        var text = messages[1].Content.GetProperty("text").GetString();
        text.ShouldStartWith("No API key found for anthropic.");
        text.ShouldContain("/login");
        messages[1].Content.GetProperty("session_id").GetString().ShouldBe("pi-session-fixture");
        process.InputClosed.ShouldBeTrue();
        process.Stopped.ShouldBeFalse();
    }

    [Test]
    public async Task A_rejected_get_state_becomes_a_terminal_failure_without_sending_the_prompt()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit("""{"id":"hagicode-get-state","type":"response","command":"get_state","success":false,"error":"state unavailable"}""");
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.ShouldHaveSingleItem();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("state unavailable");
        process.Written.Select(ScriptedPiProcess.Parse).Select(static command => command.Type).ShouldNotContain("prompt");
    }

    [Test]
    public async Task A_get_state_reply_without_a_session_id_becomes_a_terminal_failure()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit("""{"id":"hagicode-get-state","type":"response","command":"get_state","success":true,"data":{"isStreaming":false}}""");
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        messages.ShouldHaveSingleItem().Type.ShouldBe("terminal.failed");
    }

    [Test]
    public async Task A_prompt_that_an_extension_consumed_ends_the_run_without_waiting_for_agent_settled()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
            }
            else if (command.Type == "prompt")
            {
                pi.Emit("""{"id":"hagicode-prompt","type":"response","command":"prompt","success":true,"data":{"disposition":"handled"}}""");
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "/some-command");

        messages.Last().Type.ShouldBe("terminal.failed");
        process.InputClosed.ShouldBeTrue();
    }

    [Test]
    public async Task A_model_failure_after_the_prompt_was_accepted_is_a_terminal_failure_with_the_error_text()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
            }
            else if (command.Type == "prompt")
            {
                pi.Emit(PiRpcCapture.ErrorTurn);
            }
        };

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(NoAnswer), "Ask me");

        var terminal = messages.Last();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("401");
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("error");
    }

    [TestCase(nameof(PiRpcCapture.SingleQuestion))]
    [TestCase(nameof(PiRpcCapture.MultiSelectQuestion))]
    public async Task Events_are_normalized_exactly_as_the_json_path_normalizes_them(string captureName)
    {
        var capture = captureName == nameof(PiRpcCapture.SingleQuestion) ? PiRpcCapture.SingleQuestion : PiRpcCapture.MultiSelectQuestion;
        var process = new ScriptedPiProcess();
        Scenario.From(capture).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(Answer("Blue")), "Ask me");

        // The JSON path sees the same events plus the session header RPC lacks, and process exit instead of agent_settled.
        var expected = new List<CliMessage>();
        var state = new PiJsonEventMapper().CreateStreamingState(null);
        expected.AddRange(state.ProcessOutputLine("""{"type":"session","id":"pi-session-fixture"}"""));
        foreach (var line in capture)
        {
            var type = ScriptedPiProcess.Parse(line).Type;
            if (type is "response" or "extension_ui_request" or "agent_settled")
            {
                continue;
            }

            expected.AddRange(state.ProcessOutputLine(line));
        }

        expected.AddRange(state.Complete(0, string.Empty));

        var actual = messages.Where(static message => message.Type != PiUserInputRequest.MessageType).ToList();
        actual.Select(static message => message.Type).ShouldBe(expected.Select(static message => message.Type));
        actual.Select(static message => message.Content.GetRawText()).ShouldBe(expected.Select(static message => message.Content.GetRawText()));
    }

    [Test]
    public async Task The_question_tool_is_reported_as_an_ordinary_tool_lifecycle_next_to_the_question_message()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.MultiSelectQuestion).Attach(process);

        var messages = await CollectAsync(new ScriptedProvider(process), RpcOptions(Answer("Dark theme", "Mobile layout")), "Ask me");

        var types = messages.Select(static message => message.Type).ToList();
        types.ShouldContain("tool.call");
        types.ShouldContain("tool.completed");
        types.ShouldContain("assistant");
        types.IndexOf("tool.call").ShouldBeLessThan(types.IndexOf(PiUserInputRequest.MessageType));
        types.IndexOf(PiUserInputRequest.MessageType).ShouldBeLessThan(types.IndexOf("tool.completed"));

        var toolCall = messages.First(static message => message.Type == "tool.call").Content;
        toolCall.GetProperty("tool_call_id").GetString().ShouldBe("toolu-fixture-1");
        toolCall.GetProperty("tool_name").GetString().ShouldBe(PiUserInputRequest.QuestionToolName);
        var completed = messages.First(static message => message.Type == "tool.completed").Content;
        completed.GetProperty("tool_call_id").GetString().ShouldBe("toolu-fixture-1");
        string.Concat(messages.Where(static message => message.Type == "assistant").Select(static message => message.Content.GetProperty("text").GetString()))
            .ShouldContain("ANSWER=Dark theme, Mobile layout");
    }

    // ---------------------------------------------------------------- dialog routing and outcomes (3.4)

    [Test]
    public async Task A_marked_dialog_reaches_the_handler_with_every_field_and_the_caller_gets_the_question_message()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.MultiSelectQuestion).Attach(process);
        var requests = new List<PiUserInputRequest>();
        var options = RpcOptions((request, _) =>
        {
            requests.Add(request);
            return Task.FromResult<PiUserInputResponse?>(Answers(("Which features do you want?", ["Dark theme"])));
        });

        var messages = await CollectAsync(new ScriptedProvider(process), options, "Ask me");

        var request = requests.ShouldHaveSingleItem();
        request.RequestId.ShouldBe("dialog-fixture-1");
        request.ToolCallId.ShouldBe("toolu-fixture-1");
        var question = request.Questions.ShouldHaveSingleItem();
        question.Question.ShouldBe("Which features do you want?");
        question.Header.ShouldBe("Features");
        question.MultiSelect.ShouldBeTrue();
        question.Options.Select(static option => option.Label).ShouldBe(["Dark theme", "Mobile layout", "Offline mode"]);
        question.Options.Select(static option => option.Description)
            .ShouldBe(["Use a dark color scheme", "Optimize interface for mobile devices", "Enable offline functionality"]);

        var questionMessage = messages.Single(static message => message.Type == PiUserInputRequest.MessageType);
        PiUserInputRequest.TryFromCliMessage(questionMessage, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(request, new PiUserInputRequestComparer());
    }

    [Test]
    public async Task Output_keeps_streaming_while_a_question_is_open()
    {
        var process = new ScriptedPiProcess();
        var scenario = Scenario.From(PiRpcCapture.SingleQuestion);
        scenario.Attach(process);
        var answer = new TaskCompletionSource<PiUserInputResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        await ReadUntilAsync(received.Reader, message => message.Type == PiUserInputRequest.MessageType);

        // The question is open and the handler has not answered, yet a later assistant message gets through.
        process.Emit(
            """{"type":"message_end","message":{"role":"assistant","content":[{"type":"text","text":"still streaming"}],"stopReason":"toolUse"}}""");
        var streamed = await ReadUntilAsync(received.Reader, message => message.Type == "assistant");
        streamed.Content.GetProperty("text").GetString().ShouldBe("still streaming");
        answer.Task.IsCompleted.ShouldBeFalse();
        process.Written.Select(ScriptedPiProcess.Parse).ShouldNotContain(static command => command.Type == "extension_ui_response");

        answer.SetResult(Answers((SingleQuestionText, ["Blue"])));
        await run.WaitAsync(TestTimeout);
        process.Written.Select(ScriptedPiProcess.Parse).Count(static command => command.Type == "extension_ui_response").ShouldBe(1);
    }

    [Test]
    public async Task Foreign_dialogs_are_cancelled_and_fire_and_forget_requests_get_no_response()
    {
        var process = new ScriptedPiProcess();
        var handlerCalls = 0;
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
            }
            else if (command.Type == "prompt")
            {
                pi.Emit(PiRpcCapture.ForeignDialogs);
            }
        };

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<PiUserInputResponse?>(null);
            }),
            "/foreign");

        handlerCalls.ShouldBe(0);
        messages.ShouldNotContain(static message => message.Type == PiUserInputRequest.MessageType);

        // select, confirm, unmarked input, input with another wire version and editor, in the order pi asked them.
        var responses = process.Written.Select(ScriptedPiProcess.Parse).Where(static command => command.Type == "extension_ui_response").ToList();
        responses.Select(static response => response.Id).ShouldBe(
            ["dialog-fixture-6", "dialog-fixture-7", "dialog-fixture-8", "dialog-fixture-9", "dialog-fixture-10"]);
        responses.ShouldAllBe(static response => response.Root.GetProperty("cancelled").GetBoolean());
        foreach (var response in responses)
        {
            response.Root.TryGetProperty("value", out _).ShouldBeFalse();
        }

        // notify, setStatus, setWidget, setTitle and set_editor_text were answered with nothing.
        var written = process.Written.Select(ScriptedPiProcess.Parse).Select(static command => command.Type).ToList();
        written.ShouldBe(["get_state", "prompt", .. Enumerable.Repeat("extension_ui_response", 5)]);
    }

    [Test]
    public async Task A_dialog_with_a_marker_of_another_wire_version_is_not_a_question()
    {
        var process = new ScriptedPiProcess();
        var current = Scenario.From(PiRpcCapture.SingleQuestion);
        var future = current.DialogLine.Replace(PiUserInputRequest.WireMarker, "hagicode:ask_user_question:v2", StringComparison.Ordinal);
        var scenario = current with { DialogLine = future, UntilDialog = [.. current.UntilDialog.SkipLast(1), future] };
        scenario.Attach(process);
        var handlerCalls = 0;

        var messages = await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<PiUserInputResponse?>(null);
            }),
            "Ask me");

        handlerCalls.ShouldBe(0);
        messages.ShouldNotContain(static message => message.Type == PiUserInputRequest.MessageType);
        var response = process.Written.Select(ScriptedPiProcess.Parse).Single(static command => command.Type == "extension_ui_response");
        response.Root.GetProperty("cancelled").GetBoolean().ShouldBeTrue();
        messages.Last().Type.ShouldBe("terminal.completed");
    }

    public enum Outcome
    {
        HandlerThrows,
        NullAnswer,
        Timeout
    }

    [TestCase(Outcome.HandlerThrows)]
    [TestCase(Outcome.NullAnswer)]
    [TestCase(Outcome.Timeout)]
    public async Task A_question_without_an_answer_is_cancelled_exactly_once_and_the_run_continues_to_its_result(Outcome outcome)
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);
        CancellationToken handlerToken = default;
        var options = RpcOptions(
            (_, token) =>
            {
                handlerToken = token;
                return outcome switch
                {
                    Outcome.HandlerThrows => throw new InvalidOperationException("handler failed"),
                    Outcome.NullAnswer => Task.FromResult<PiUserInputResponse?>(null),
                    _ => new TaskCompletionSource<PiUserInputResponse?>().Task
                };
            },
            outcome == Outcome.Timeout ? TimeSpan.FromMilliseconds(150) : null);

        var messages = await CollectAsync(new ScriptedProvider(process), options, "Ask me");

        var response = process.Written.Select(ScriptedPiProcess.Parse).Single(static command => command.Type == "extension_ui_response");
        response.Id.ShouldBe("dialog-fixture-1");
        response.Root.GetProperty("cancelled").GetBoolean().ShouldBeTrue();
        response.Root.TryGetProperty("value", out _).ShouldBeFalse();
        messages.Last().Type.ShouldBe("terminal.completed");
        if (outcome == Outcome.Timeout)
        {
            handlerToken.IsCancellationRequested.ShouldBeTrue();
        }
    }

    [Test]
    public async Task An_answered_question_is_answered_exactly_once_with_the_answers_as_the_dialog_value()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);

        await CollectAsync(new ScriptedProvider(process), RpcOptions(Answer("Blue")), "Ask me");

        var response = process.Written.Select(ScriptedPiProcess.Parse).Single(static command => command.Type == "extension_ui_response");
        response.Id.ShouldBe("dialog-fixture-1");
        response.Root.TryGetProperty("cancelled", out _).ShouldBeFalse();
        var value = JsonDocument.Parse(response.Root.GetProperty("value").GetString()!).RootElement;
        value.GetProperty("answers").GetProperty(SingleQuestionText).GetString().ShouldBe("Blue");
    }

    [Test]
    public async Task A_multi_select_answer_is_one_string_with_the_values_joined_by_a_comma_and_a_space()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.MultiSelectQuestion).Attach(process);

        await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((request, _) => Task.FromResult<PiUserInputResponse?>(
                Answers((request.Questions[0].Question, ["Dark theme", "Mobile layout"])))),
            "Ask me");

        // This is the exact answer string the real run was fed (the model quoted it back unchanged).
        var response = process.Written.Select(ScriptedPiProcess.Parse).Single(static command => command.Type == "extension_ui_response");
        response.Root.GetProperty("value").GetString().ShouldBe("""{"answers":{"Which features do you want?":"Dark theme, Mobile layout"}}""");
    }

    [Test]
    public async Task A_free_text_answer_that_matches_no_option_is_passed_through_unchanged_on_one_line()
    {
        const string FreeText = "Green, \"with quotes\"\nsecond line \u2028 and 界 \\ backslash";
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);

        await CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((request, _) => Task.FromResult<PiUserInputResponse?>(Answers((request.Questions[0].Question, [FreeText])))),
            "Ask me");

        process.Written.ShouldAllBe(static line => !line.Contains('\n') && !line.Contains('\r'));
        var response = process.Written.Select(ScriptedPiProcess.Parse).Single(static command => command.Type == "extension_ui_response");
        var value = JsonDocument.Parse(response.Root.GetProperty("value").GetString()!).RootElement;
        value.GetProperty("answers").GetProperty(SingleQuestionText).GetString().ShouldBe(FreeText);
    }

    [Test]
    public async Task A_dialog_request_that_repeats_an_open_dialog_id_is_ignored()
    {
        var process = new ScriptedPiProcess();
        var scenario = Scenario.From(PiRpcCapture.SingleQuestion);
        scenario = scenario with { UntilDialog = [.. scenario.UntilDialog, scenario.DialogLine] };
        scenario.Attach(process);
        var handlerCalls = 0;
        var gate = new TaskCompletionSource<PiUserInputResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = CollectAsync(
            new ScriptedProvider(process),
            RpcOptions((_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return gate.Task;
            }),
            "Ask me");
        await WaitUntilAsync(() => handlerCalls > 0);
        await Task.Delay(100);
        gate.SetResult(Answers((SingleQuestionText, ["Blue"])));
        var messages = await run;

        handlerCalls.ShouldBe(1);
        messages.Count(static message => message.Type == PiUserInputRequest.MessageType).ShouldBe(1);
        process.Written.Select(ScriptedPiProcess.Parse).Count(static command => command.Type == "extension_ui_response").ShouldBe(1);
    }

    // ---------------------------------------------------------------- cancellation (3.5)

    [Test]
    public async Task Cancelling_while_a_question_is_open_dismisses_the_dialog_aborts_the_run_and_ends_the_process()
    {
        var process = new ScriptedPiProcess();
        var scenario = Scenario.From(PiRpcCapture.AbortWithPendingDialogBefore, PiRpcCapture.AbortWithPendingDialogAfter);
        scenario.Attach(process, emitAfterOn: "abort");
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
                                   return new TaskCompletionSource<PiUserInputResponse?>().Task;
                               }),
                               "Ask me",
                               cancellation.Token))
            {
                await received.Writer.WriteAsync(message);
            }
        });

        await handlerStarted.Task.WaitAsync(TestTimeout);
        await ReadUntilAsync(received.Reader, message => message.Type == PiUserInputRequest.MessageType);
        await cancellation.CancelAsync();

        await AssertCanceledAsync(run);

        // The dialog is dismissed before the abort, standard input is closed and the process has ended.
        var written = process.Written.Select(ScriptedPiProcess.Parse).ToList();
        written.Select(static command => command.Type).ShouldBe(["get_state", "prompt", "extension_ui_response", "abort"]);
        written[2].Id.ShouldBe("dialog-fixture-1");
        written[2].Root.GetProperty("cancelled").GetBoolean().ShouldBeTrue();
        handlerToken.IsCancellationRequested.ShouldBeTrue();
        process.InputClosed.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
        process.Stopped.ShouldBeFalse();
        process.Disposed.ShouldBeTrue();
    }

    [Test]
    public async Task Cancelling_a_run_that_has_no_open_dialog_aborts_it_and_ends_the_process()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
            }
            else if (command.Type == "prompt")
            {
                pi.Emit("""{"id":"hagicode-prompt","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""");
            }
        };
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider(process);
        var received = Channel.CreateUnbounded<CliMessage>();
        var run = Task.Run(async () =>
        {
            await foreach (var message in provider.ExecuteAsync(RpcOptions(NoAnswer), "Ask me", cancellation.Token))
            {
                await received.Writer.WriteAsync(message);
            }
        });

        await ReadUntilAsync(received.Reader, message => message.Type == "session.started");
        await WaitUntilAsync(() => process.Written.Count >= 2);
        await cancellation.CancelAsync();

        await AssertCanceledAsync(run);
        process.Written.Select(ScriptedPiProcess.Parse).Select(static command => command.Type).ShouldBe(["get_state", "prompt", "abort"]);
        process.InputClosed.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
    }

    [Test]
    public async Task Cancelling_stops_a_process_that_does_not_exit_after_the_abort_and_still_surfaces_the_cancellation()
    {
        var process = new ScriptedPiProcess { ExitOnInputClose = false };
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
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

        await ReadUntilAsync(received.Reader, message => message.Type == "session.started");
        await cancellation.CancelAsync();

        await AssertCanceledAsync(run);
        process.Stopped.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
    }

    [Test]
    public async Task A_consumer_that_stops_enumerating_aborts_the_run_and_ends_the_process()
    {
        var process = new ScriptedPiProcess();
        Scenario.From(PiRpcCapture.SingleQuestion).Attach(process);
        var provider = new ScriptedProvider(process);

        await foreach (var message in provider.ExecuteAsync(RpcOptions(NoAnswer), "Ask me"))
        {
            message.Type.ShouldBe("session.started");
            break;
        }

        process.Written.Select(ScriptedPiProcess.Parse).Select(static command => command.Type).ShouldContain("abort");
        process.InputClosed.ShouldBeTrue();
        process.HasExited.ShouldBeTrue();
        process.Disposed.ShouldBeTrue();
    }

    // ---------------------------------------------------------------- duplicate tool name (1.2, 2.8)

    [Test]
    public async Task A_user_extension_that_registers_the_question_tool_makes_the_run_fall_back_to_the_print_path()
    {
        var process = new ScriptedPiProcess();
        process.OnStart = static pi => pi.Exit(1, PiRpcCapture.QuestionToolConflictStandardError);
        var logger = new CapturingLogger();
        var provider = new ScriptedProvider(process, logger);

        var messages = await CollectAsync(provider, RpcOptions(NoAnswer), "Ask me");

        // Nothing came out of the RPC attempt; the request ran the way it runs without a handler.
        provider.PrintContext.ShouldNotBeNull();
        provider.PrintContext.Arguments.ShouldBe(["--mode", "json", "--print", "--model", "mock-model", "--no-session", "Ask me"]);
        messages.ShouldHaveSingleItem().Type.ShouldBe("terminal.completed");
        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Warning);
        logger.Entries[0].Message.ShouldContain("ask_user_question");
        logger.Entries[0].Message.ShouldContain("conflicts with");
    }

    [Test]
    public async Task A_startup_failure_that_is_not_the_tool_conflict_is_reported_not_retried()
    {
        var process = new ScriptedPiProcess();
        process.OnStart = static pi => pi.Exit(1, "Error: Unknown provider \"nope\".\n");
        var provider = new ScriptedProvider(process);

        var messages = await CollectAsync(provider, RpcOptions(NoAnswer), "Ask me");

        provider.PrintContext.ShouldBeNull();
        var terminal = messages.ShouldHaveSingleItem();
        terminal.Type.ShouldBe("terminal.failed");
        var terminalText = terminal.Content.GetProperty("text").GetString();
        terminalText.ShouldNotBeNull();
        terminalText.ShouldContain("Unknown provider");
        terminal.Content.GetProperty("exit_code").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task The_tool_conflict_text_after_pi_started_talking_is_a_failure_not_a_fallback()
    {
        var process = new ScriptedPiProcess();
        process.OnWrite = static (pi, command) =>
        {
            if (command.Type == "get_state")
            {
                pi.Emit(PiRpcCapture.GetStateReply);
                pi.Exit(1, PiRpcCapture.QuestionToolConflictStandardError);
            }
        };
        var provider = new ScriptedProvider(process);

        var messages = await CollectAsync(provider, RpcOptions(NoAnswer), "Ask me");

        provider.PrintContext.ShouldBeNull();
        messages.Last().Type.ShouldBe("terminal.failed");
    }

    // ---------------------------------------------------------------- extension extraction (2.5, 3.5)

    [Test]
    public void The_extension_is_embedded_and_names_the_tool_and_the_wire_marker_the_provider_expects()
    {
        using var directory = new ScratchDirectory();

        var path = PiQuestionExtension.EnsureExtracted(directory.Path);

        var source = File.ReadAllText(path);
        source.ShouldContain($"\"{PiUserInputRequest.WireMarker}\"");
        source.ShouldContain($"name: \"{PiUserInputRequest.QuestionToolName}\"");
        source.ShouldContain("toolCallId");
        source.ShouldContain("answers");
    }

    [Test]
    public void Extraction_writes_to_a_directory_named_after_the_content_hash()
    {
        using var directory = new ScratchDirectory();

        var path = PiQuestionExtension.EnsureExtracted(directory.Path);

        var content = File.ReadAllBytes(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content))[..16];
        path.ShouldBe(Path.Combine(directory.Path, hash, "ask-user-question.ts"));
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldBe([path]);
    }

    [Test]
    public void Extraction_is_idempotent_and_leaves_an_intact_file_alone()
    {
        using var directory = new ScratchDirectory();
        var path = PiQuestionExtension.EnsureExtracted(directory.Path);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var again = PiQuestionExtension.EnsureExtracted(directory.Path);

        again.ShouldBe(path);
        File.GetLastWriteTimeUtc(path).ShouldBe(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void Extraction_restores_a_missing_or_damaged_file()
    {
        using var directory = new ScratchDirectory();
        var path = PiQuestionExtension.EnsureExtracted(directory.Path);
        var original = File.ReadAllBytes(path);

        File.WriteAllText(path, "// tampered");
        PiQuestionExtension.EnsureExtracted(directory.Path).ShouldBe(path);
        File.ReadAllBytes(path).ShouldBe(original);

        File.Delete(path);
        PiQuestionExtension.EnsureExtracted(directory.Path).ShouldBe(path);
        File.ReadAllBytes(path).ShouldBe(original);
    }

    [Test]
    public async Task Concurrent_extraction_agrees_on_one_complete_file_and_leaves_nothing_behind()
    {
        using var directory = new ScratchDirectory();

        var paths = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            Task.Run(() => PiQuestionExtension.EnsureExtracted(directory.Path))));

        var path = paths.Distinct().ShouldHaveSingleItem();
        var content = File.ReadAllBytes(path);
        Convert.ToHexStringLower(SHA256.HashData(content))[..16].ShouldBe(new DirectoryInfo(Path.GetDirectoryName(path)!).Name);
        Directory.GetFiles(Path.GetDirectoryName(path)!).ShouldBe([path]);
    }

    // ---------------------------------------------------------------- a real process (the default IPiRpcProcess)

    [Test]
    public async Task ExecuteAsync_runs_the_rpc_conversation_over_a_real_process()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var scenario = Scenario.From(PiRpcCapture.SingleQuestion);
        using var directory = new ScratchDirectory();
        var statePath = Path.Combine(directory.Path, "state.jsonl");
        var untilPath = Path.Combine(directory.Path, "until.jsonl");
        var afterPath = Path.Combine(directory.Path, "after.jsonl");
        var receivedPath = Path.Combine(directory.Path, "received.txt");
        await File.WriteAllLinesAsync(statePath, [scenario.StateReply]);
        await File.WriteAllLinesAsync(untilPath, scenario.UntilDialog);
        await File.WriteAllLinesAsync(afterPath, scenario.AfterDialog);

        // The script plays pi: it answers each command it reads, records what it was sent, and exits only when its standard
        // input ends, as pi does.
        var script = directory.WriteScript(
            "fake-pi.sh",
            """
            IFS= read -r get_state
            cat "$1"
            IFS= read -r prompt
            cat "$2"
            IFS= read -r response
            printf '%s\n' "$get_state" "$prompt" "$response" > "$4"
            cat "$3"
            cat >/dev/null
            exit 0
            """);
        var manager = new ScriptProcessManager(script, [statePath, untilPath, afterPath, receivedPath]);
        var provider = new PiProvider(new StubExecutableResolver(), manager);

        var messages = await CollectAsync(provider, RpcOptions(Answer("Blue")), "Ask me");

        messages.Last().Type.ShouldBe("terminal.completed");
        messages.ShouldContain(static message => message.Type == PiUserInputRequest.MessageType);
        var received = (await File.ReadAllLinesAsync(receivedPath)).Select(ScriptedPiProcess.Parse).ToList();
        received.Select(static command => command.Type).ShouldBe(["get_state", "prompt", "extension_ui_response"]);
        received[0].Id.ShouldBe("hagicode-get-state");
        received[1].Root.GetProperty("message").GetString().ShouldBe("Ask me");
        received[2].Id.ShouldBe("dialog-fixture-1");
        manager.LastContext!.Arguments.Take(2).ShouldBe(["--mode", "rpc"]);
    }

    // ---------------------------------------------------------------- helpers

    private static PiOptions RpcOptions(
        Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>> handler,
        TimeSpan? questionTimeout = null)
    {
        return new PiOptions
        {
            Model = "mock-model",
            NoSession = true,
            UserInputHandler = handler,
            QuestionTimeout = questionTimeout ?? PiOptions.DefaultQuestionTimeout
        };
    }

    private static Func<PiUserInputRequest, CancellationToken, Task<PiUserInputResponse?>> Answer(params string[] values)
    {
        return (request, _) => Task.FromResult<PiUserInputResponse?>(Answers((request.Questions[0].Question, values)));
    }

    private static PiUserInputResponse Answers(params (string Question, string[] Values)[] answers)
    {
        return new PiUserInputResponse(
            answers.ToDictionary(static pair => pair.Question, static pair => (IReadOnlyList<string>)pair.Values));
    }

    private static async Task<List<CliMessage>> CollectAsync(
        PiProvider provider,
        PiOptions options,
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

    private static Task AssertCanceledAsync(Task run)
    {
        var exception = Assert.CatchAsync<OperationCanceledException>(() => run.WaitAsync(TestTimeout));
        exception.ShouldBeAssignableTo<OperationCanceledException>();
        return Task.CompletedTask;
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

    /// <summary>The conversation of a capture: what pi answers to get_state, to the prompt and to the answer.</summary>
    private sealed record Scenario(
        string StateReply,
        IReadOnlyList<string> UntilDialog,
        string DialogLine,
        IReadOnlyList<string> AfterDialog)
    {
        public static Scenario From(IReadOnlyList<string> capture)
        {
            var dialog = capture.ToList().FindIndex(static line => line.Contains("\"type\":\"extension_ui_request\"", StringComparison.Ordinal));
            return new Scenario(capture[0], capture.Skip(1).Take(dialog).ToArray(), capture[dialog], capture.Skip(dialog + 1).ToArray());
        }

        public static Scenario From(IReadOnlyList<string> untilDialog, IReadOnlyList<string> afterDialog)
        {
            return new Scenario(PiRpcCapture.GetStateReply[0], untilDialog, untilDialog[^1], afterDialog);
        }

        /// <summary>
        /// Makes the process answer get_state, then the prompt up to the dialog, then (on the host's answer, or on
        /// <paramref name="emitAfterOn" />) the rest of the run.
        /// </summary>
        public void Attach(ScriptedPiProcess process, string emitAfterOn = "extension_ui_response")
        {
            var emittedAfter = 0;
            process.OnWrite = (pi, command) =>
            {
                if (command.Type == "get_state")
                {
                    pi.Emit(StateReply);
                }
                else if (command.Type == "prompt")
                {
                    pi.Emit(UntilDialog);
                }
                else if (command.Type == emitAfterOn && Interlocked.Exchange(ref emittedAfter, 1) == 0)
                {
                    pi.Emit(AfterDialog);
                }
            };
        }
    }

    private sealed class PiUserInputRequestComparer : IEqualityComparer<PiUserInputRequest>
    {
        public bool Equals(PiUserInputRequest? x, PiUserInputRequest? y)
        {
            return x is not null && y is not null
                   && x.RequestId == y.RequestId
                   && x.ToolCallId == y.ToolCallId
                   && x.Questions.Count == y.Questions.Count
                   && x.Questions.Zip(y.Questions).All(static pair =>
                       pair.First.Question == pair.Second.Question
                       && pair.First.Header == pair.Second.Header
                       && pair.First.MultiSelect == pair.Second.MultiSelect
                       && pair.First.Options.SequenceEqual(pair.Second.Options));
        }

        public int GetHashCode(PiUserInputRequest obj) => obj.RequestId.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>
    /// A pi process played by the test: lines queued with <see cref="Emit(string)" /> are its standard output, every line the
    /// host writes is recorded and handed to <see cref="OnWrite" />, and closing standard input ends it like pi does.
    /// </summary>
    private sealed class ScriptedPiProcess : IPiRpcProcess
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _standardError = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _written = [];
        private readonly object _gate = new();

        /// <summary>Called, on the writing thread, for every line the host writes.</summary>
        public Action<ScriptedPiProcess, Command>? OnWrite { get; set; }

        /// <summary>Called once, when the provider starts to use the process.</summary>
        public Action<ScriptedPiProcess>? OnStart { get; set; }

        /// <summary>Whether closing standard input makes the process exit with code 0, as pi does.</summary>
        public bool ExitOnInputClose { get; init; } = true;

        public bool InputClosed { get; private set; }

        public bool Stopped { get; private set; }

        public bool Disposed { get; private set; }

        public bool HasExited => _exit.Task.IsCompleted;

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
            if (!HasExited)
            {
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

        public void Exit(int exitCode, string standardError = "")
        {
            _output.Writer.TryComplete();
            _standardError.TrySetResult(standardError);
            _exit.TrySetResult(exitCode);
        }

        public void Start() => OnStart?.Invoke(this);

        public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasExited || InputClosed)
            {
                throw new IOException("Broken pipe");
            }

            lock (_gate)
            {
                _written.Add(line);
            }

            OnWrite?.Invoke(this, Parse(line));
            return ValueTask.CompletedTask;
        }

        public void CloseInput()
        {
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

    /// <summary>A provider whose RPC process is the scripted one and whose print path is a canned completion.</summary>
    private sealed class ScriptedProvider : PiProvider
    {
        private readonly ScriptedPiProcess _process;

        public ScriptedProvider(ScriptedPiProcess process, ILogger<PiProvider>? logger = null)
            : base(new StubExecutableResolver(), new CliProcessManager(), null, logger)
        {
            _process = process;
        }

        public ProcessStartContext? RpcContext { get; private set; }

        public ProcessStartContext? PrintContext { get; private set; }

        public int RpcStarts { get; private set; }

        internal override ValueTask<IPiRpcProcess> StartRpcProcessAsync(ProcessStartContext startContext, CancellationToken cancellationToken)
        {
            RpcContext = startContext;
            RpcStarts++;
            _process.Start();
            return ValueTask.FromResult<IPiRpcProcess>(_process);
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

    private sealed class CapturingLogger : ILogger<PiProvider>
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-pi-question-{Guid.NewGuid():N}");
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
