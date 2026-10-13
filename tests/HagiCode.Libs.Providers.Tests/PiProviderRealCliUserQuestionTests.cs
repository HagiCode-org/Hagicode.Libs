using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shouldly;
using Xunit.Abstractions;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Pi;
using Microsoft.Extensions.Logging;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Contract tests of the interactive question path against a real <c>pi</c> executable and a real model. They spend a few
/// model calls each, so they run only when <c>HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION</c> is <c>1</c> or <c>true</c>.
/// <c>HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION_MODEL</c> picks the model selector (use a cheap one, for example
/// <c>omniroute/cc/claude-haiku-4-5-20251001</c>); without it pi uses its configured default model.
/// <c>HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION_EXECUTABLE</c> picks a specific executable, otherwise <c>pi</c> is resolved from
/// <c>PATH</c>. The pi version is written to the test output. Credentials come from the environment and the pi home of the
/// current user, which every run reaches through an isolated <c>PI_CODING_AGENT_DIR</c> that only links its model and
/// settings files, so the runs never touch the user's extensions or sessions: no session is persisted and the working
/// directory is a scratch directory.
/// </summary>
public sealed class PiProviderRealCliUserQuestionTests(ITestOutputHelper output)
{
    private const string SwitchVariable = "HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION";
    private const string ExecutableVariable = "HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION_EXECUTABLE";
    private const string ModelVariable = "HAGICODE_LIBS_REAL_CLI_PI_USER_QUESTION_MODEL";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task A_multi_select_question_is_asked_answered_and_the_agent_receives_the_comma_joined_answer()
    {
        if (!IsEnabled())
        {
            return;
        }

        using var home = new IsolatedPiHome();
        var requests = new List<PiUserInputRequest>();
        var options = CreateOptions(home) with
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                var question = request.Questions.Single();
                return Task.FromResult<PiUserInputResponse?>(new PiUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [question.Question] = ["Dark theme", "Mobile layout"] }));
            }
        };

        var run = await RunAsync(
            options,
            "Use the ask_user_question tool exactly once with ONE question that has multiSelect true and three options: Dark theme, Mobile layout, Offline mode. Ask which features I want. After I answer, reply with exactly: ANSWER=<the answer string you received>. Do not use any other tool.");

        var request = requests.ShouldHaveSingleItem();
        var question = request.Questions.ShouldHaveSingleItem();
        question.MultiSelect.ShouldBeTrue();
        question.Options.Select(static option => option.Label).ShouldBe(["Dark theme", "Mobile layout", "Offline mode"], ignoreOrder: true);
        question.Options.ShouldAllBe(static option => option.Description.Length > 0);
        request.ToolCallId.ShouldNotBeNullOrWhiteSpace();
        request.ToolCallId.ShouldNotBe(request.RequestId);

        run.Completed.ShouldBeTrue(run.Describe());
        var types = run.Messages.Select(static message => message.Type).ToList();
        types[0].ShouldBe("session.started");
        types.IndexOf("tool.call").ShouldBeLessThan(types.IndexOf(PiUserInputRequest.MessageType));
        types.IndexOf(PiUserInputRequest.MessageType).ShouldBeLessThan(types.IndexOf("tool.completed"));
        var toolCall = run.Messages.First(static message => message.Type == "tool.call").Content;
        toolCall.GetProperty("tool_call_id").GetString().ShouldBe(request.ToolCallId);
        toolCall.GetProperty("tool_name").GetString().ShouldBe(PiUserInputRequest.QuestionToolName);

        // The answer reached the agent as the one comma-joined string, which the agent quotes back.
        run.AssistantText.ShouldContain("Dark theme, Mobile layout", Case.Sensitive);
        run.AssertNoPiProcessLeft();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task A_question_answered_after_more_than_sixty_seconds_continues_the_run()
    {
        if (!IsEnabled())
        {
            return;
        }

        using var home = new IsolatedPiHome();
        var asked = Stopwatch.StartNew();
        TimeSpan answeredAfter = default;
        var options = CreateOptions(home) with
        {
            UserInputHandler = async (request, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(65), token);
                answeredAfter = asked.Elapsed;
                var question = request.Questions.Single();
                return new PiUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [question.Question] = [question.Options[^1].Label] });
            }
        };

        var run = await RunAsync(
            options,
            "Use the ask_user_question tool exactly once to ask me which colour I prefer: Red or Blue (single choice, 2 options, Red first). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool.",
            timeout: TimeSpan.FromSeconds(240));

        answeredAfter.ShouldBeGreaterThan(TimeSpan.FromSeconds(60));
        run.Completed.ShouldBeTrue(run.Describe());
        run.AssistantText.ShouldContain("Blue", Case.Insensitive);
        run.AssertNoPiProcessLeft();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task Cancelling_while_a_question_is_pending_ends_the_run_and_leaves_no_pi_process()
    {
        if (!IsEnabled())
        {
            return;
        }

        using var home = new IsolatedPiHome();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken = default;
        var options = CreateOptions(home) with
        {
            UserInputHandler = (_, token) =>
            {
                handlerToken = token;
                handlerStarted.TrySetResult();
                return new TaskCompletionSource<PiUserInputResponse?>().Task;
            }
        };
        var manager = new RecordingProcessManager();
        var provider = new PiProvider(new CliExecutableResolver(), manager);
        using var cancellation = new CancellationTokenSource(RunTimeout);
        var messages = new List<CliMessage>();
        var cancelled = Stopwatch.StartNew();

        var act = async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               options,
                               "Use the ask_user_question tool exactly once to ask me which colour I prefer: Red or Blue. Then reply with exactly: DONE.",
                               cancellation.Token))
            {
                messages.Add(message);
                if (message.Type == PiUserInputRequest.MessageType)
                {
                    await handlerStarted.Task;
                    await Task.Delay(500);
                    cancelled.Restart();
                    await cancellation.CancelAsync();
                }
            }
        };

        var exception = await Record.ExceptionAsync(act);
        output.WriteLine($"cancel took {cancelled.Elapsed.TotalMilliseconds:F0} ms; pi process stops requested: {manager.StopCalls}; pids: {string.Join(",", manager.Pids)}");

        exception.ShouldBeAssignableTo<OperationCanceledException>();
        handlerToken.IsCancellationRequested.ShouldBeTrue();
        messages.ShouldContain(static message => message.Type == PiUserInputRequest.MessageType);
        manager.Pids.ShouldHaveSingleItem();
        PiProcessIsRunning(manager.Pids[0]).ShouldBeFalse("the pi process must be gone when ExecuteAsync returns");
        cancelled.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task A_user_extension_in_the_pi_home_still_works_its_dialogs_are_cancelled_and_questions_are_still_asked()
    {
        if (!IsEnabled())
        {
            return;
        }

        using var home = new IsolatedPiHome();
        home.WriteExtension("user-dialogs.ts", UserDialogsExtension);
        var requests = new List<PiUserInputRequest>();
        var options = CreateOptions(home) with
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                var question = request.Questions.Single();
                return Task.FromResult<PiUserInputResponse?>(new PiUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [question.Question] = [question.Options[^1].Label] }));
            }
        };

        var run = await RunAsync(
            options,
            "Step 1: call the user_dialogs tool once and read its result. Step 2: use the ask_user_question tool exactly once to ask me which colour I prefer: Red or Blue (single choice, 2 options, Red first). Step 3: reply with exactly: DONE <the user_dialogs result> answer=<my choice>. Do not use any other tool.");

        run.Completed.ShouldBeTrue(run.Describe());

        // The user extension's dialogs were dismissed, never offered to the handler; the question still arrived.
        requests.ShouldHaveSingleItem();
        run.Messages.Count(static message => message.Type == PiUserInputRequest.MessageType).ShouldBe(1);
        run.AssistantText.ShouldContain("select=undefined", Case.Insensitive);
        run.AssistantText.ShouldContain("confirm=false", Case.Insensitive);
        run.AssistantText.ShouldContain("Blue", Case.Insensitive);
        run.AssertNoPiProcessLeft();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task A_user_extension_that_registers_the_question_tool_makes_the_run_fall_back_to_print_mode()
    {
        if (!IsEnabled())
        {
            return;
        }

        using var home = new IsolatedPiHome();
        home.WriteExtension("duplicate-question-tool.ts", DuplicateQuestionToolExtension);
        var handlerCalls = 0;
        var options = CreateOptions(home) with
        {
            UserInputHandler = (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<PiUserInputResponse?>(null);
            }
        };
        var logger = new CapturingLogger();

        var run = await RunAsync(options, "Reply with exactly: PONG. Do not use any tool.", logger: logger);

        run.Completed.ShouldBeTrue(run.Describe());
        run.AssistantText.ShouldContain("PONG", Case.Insensitive);
        handlerCalls.ShouldBe(0);
        run.Messages.ShouldNotContain(static message => message.Type == PiUserInputRequest.MessageType);
        logger.Warnings.ShouldHaveSingleItem().ShouldContain("conflicts with", Case.Sensitive);
        run.Starts.ShouldBe(2, "the RPC attempt that pi refused, then the print path");
        run.AssertNoPiProcessLeft();
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task A_large_question_payload_and_a_large_answer_travel_through_the_dialog()
    {
        if (!IsEnabled())
        {
            return;
        }

        const int PayloadChars = 400_000;
        const int AnswerChars = 200_000;
        using var home = new IsolatedPiHome();
        home.WriteExtension("big-question.ts", BigQuestionExtension(PayloadChars));
        var requests = new List<PiUserInputRequest>();
        var options = CreateOptions(home) with
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                var question = request.Questions.Single();
                return Task.FromResult<PiUserInputResponse?>(new PiUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [question.Question] = [new string('z', AnswerChars)] }));
            }
        };

        var run = await RunAsync(
            options,
            "Call the big_question tool exactly once with no arguments, then reply with exactly the text it returned. Do not use any other tool.");

        run.Completed.ShouldBeTrue(run.Describe());
        var question = requests.ShouldHaveSingleItem().Questions.ShouldHaveSingleItem();
        var description = question.Options[0].Description;
        description.Length.ShouldBe(PayloadChars);
        description.ShouldContain((char)0x2028);
        description.ShouldContain("界");
        run.AssistantText.ShouldContain($"BIG-ANSWER-LEN={AnswerChars}", Case.Sensitive);
        run.AssertNoPiProcessLeft();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<RunResult> RunAsync(
        PiOptions options,
        string prompt,
        TimeSpan? timeout = null,
        ILogger<PiProvider>? logger = null)
    {
        var executable = options.ExecutablePath!;
        output.WriteLine($"pi executable: {executable}");
        output.WriteLine($"pi version: {await ReadVersionAsync(executable)}");
        output.WriteLine($"model: {options.Model ?? "(pi default)"}");

        var manager = new RecordingProcessManager();
        var provider = new PiProvider(new CliExecutableResolver(), manager, null, logger);
        using var cancellation = new CancellationTokenSource(timeout ?? RunTimeout);
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellation.Token))
        {
            messages.Add(message);
        }

        var result = new RunResult(messages, manager);
        output.WriteLine(result.Describe());
        return result;
    }

    private static PiOptions CreateOptions(IsolatedPiHome home)
    {
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        return new PiOptions
        {
            ExecutablePath = ResolveExecutable(),
            WorkingDirectory = home.WorkingDirectory,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            NoSession = true,
            QuestionTimeout = TimeSpan.FromMinutes(5),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["PI_CODING_AGENT_DIR"] = home.AgentDirectory,
                ["PI_OFFLINE"] = "1",
                ["PI_SKIP_VERSION_CHECK"] = "1",
                ["PI_TELEMETRY"] = "0"
            }
        };
    }

    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return new CliExecutableResolver().ResolveFirstAvailablePath(["pi"])
               ?? throw new InvalidOperationException("The real CLI lane needs a pi executable on PATH.");
    }

    private static async Task<string> ReadVersionAsync(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        var standardError = process.StandardError.ReadToEndAsync();
        var version = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (version + await standardError).Trim();
    }

    private static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(SwitchVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PiProcessIsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private const string UserDialogsExtension = """
        import { Type } from "@earendil-works/pi-ai";
        import { defineTool, type ExtensionAPI } from "@earendil-works/pi-coding-agent";

        // A user extension that opens dialogs of its own during a run. Under RPC they reach the host.
        export default function (pi: ExtensionAPI) {
        	pi.registerTool(
        		defineTool({
        			name: "user_dialogs",
        			label: "User dialogs",
        			description: "Opens a few dialogs and reports what came back.",
        			parameters: Type.Object({}),
        			async execute(_id, _params, _signal, _onUpdate, ctx) {
        				ctx.ui.notify("hello from a user extension", "info");
        				ctx.ui.setStatus("user-dialogs", "working");
        				const selected = await ctx.ui.select("Pick one", ["a", "b"]);
        				const confirmed = await ctx.ui.confirm("Sure?", "really");
        				const typed = await ctx.ui.input("Free text", "type something");
        				return { content: [{ type: "text", text: `select=${selected} confirm=${confirmed} input=${typed}` }], details: {} };
        			},
        		}),
        	);
        }
        """;

    private const string DuplicateQuestionToolExtension = """
        import { Type } from "@earendil-works/pi-ai";
        import { defineTool, type ExtensionAPI } from "@earendil-works/pi-coding-agent";

        // A user extension that registers the same tool name as the bundled one.
        export default function (pi: ExtensionAPI) {
        	pi.registerTool(
        		defineTool({
        			name: "ask_user_question",
        			label: "User's own question tool",
        			description: "A different tool that happens to have the same name.",
        			parameters: Type.Object({ prompt: Type.String() }),
        			async execute() {
        				return { content: [{ type: "text", text: "unused" }], details: {} };
        			},
        		}),
        	);
        }
        """;

    private static string BigQuestionExtension(int payloadChars)
    {
        return $$"""
            import { Type } from "@earendil-works/pi-ai";
            import { defineTool, type ExtensionAPI } from "@earendil-works/pi-coding-agent";

            // Sends one huge question through the same versioned dialog the bundled extension uses.
            const MARKER = "hagicode:ask_user_question:v1";
            export default function (pi: ExtensionAPI) {
            	pi.registerTool(
            		defineTool({
            			name: "big_question",
            			label: "Big question",
            			description: "Asks one question with a very large option description.",
            			parameters: Type.Object({}),
            			async execute(toolCallId, _params, signal, _onUpdate, ctx) {
            				const filler = ("界x" + String.fromCharCode(0x2028) + "é").repeat(Math.ceil({{payloadChars}} / 4)).slice(0, {{payloadChars}});
            				const payload = { toolCallId, questions: [{ question: "Big?", header: "Big", multiSelect: false, options: [{ label: "A", description: filler }, { label: "B", description: "small" }] }] };
            				const value = await ctx.ui.input(`${MARKER}\n${JSON.stringify(payload)}`, "answers", { signal });
            				if (value === undefined) return { content: [{ type: "text", text: "BIG-DECLINED" }], details: {} };
            				const answer = JSON.parse(value).answers["Big?"];
            				return { content: [{ type: "text", text: `BIG-ANSWER-LEN=${answer.length}` }], details: {} };
            			},
            		}),
            	);
            }
            """;
    }

    private sealed class RunResult(IReadOnlyList<CliMessage> messages, RecordingProcessManager manager)
    {
        public IReadOnlyList<CliMessage> Messages { get; } = messages;

        public int Starts => manager.Pids.Count;

        public bool Completed => Messages.Count > 0 && Messages[^1].Type == "terminal.completed";

        public string AssistantText => string.Concat(Messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.TryGetProperty("text", out var text) ? text.GetString() : null));

        public void AssertNoPiProcessLeft()
        {
            manager.Pids.ShouldNotBeEmpty();
            foreach (var pid in manager.Pids)
            {
                PiProcessIsRunning(pid).ShouldBeFalse($"pi process {pid} is still running");
            }
        }

        public string Describe()
        {
            var builder = new StringBuilder();
            foreach (var message in Messages)
            {
                var text = message.Content.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
                builder.Append(message.Type);
                if (text is { Length: > 0 })
                {
                    builder.Append(": ").Append(text.Length > 160 ? text[..160] + "..." : text);
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }
    }

    /// <summary>Starts processes like the default manager and remembers their ids and how often a stop was asked for.</summary>
    private sealed class RecordingProcessManager : CliProcessManager
    {
        public List<int> Pids { get; } = [];

        public int StopCalls { get; private set; }

        public override async ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            var handle = await base.StartAsync(context, cancellationToken);
            Pids.Add(handle.Process.Id);
            return handle;
        }

        public override Task StopAsync(CliProcessHandle? handle, CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return base.StopAsync(handle, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<PiProvider>
    {
        public List<string> Warnings { get; } = [];

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
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// A pi agent directory of its own: it links the model and settings files (and the credentials file, when there is one) of
    /// the current user's pi home and has a scratch <c>extensions</c> folder, so a test can plant user extensions without
    /// touching the real home.
    /// </summary>
    private sealed class IsolatedPiHome : IDisposable
    {
        private static readonly string[] LinkedFiles = ["models.json", "settings.json", "auth.json"];

        private readonly string _root;

        public IsolatedPiHome()
        {
            _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-pi-real-{Guid.NewGuid():N}");
            AgentDirectory = Path.Combine(_root, "agent");
            WorkingDirectory = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(Path.Combine(AgentDirectory, "extensions"));
            Directory.CreateDirectory(WorkingDirectory);

            var sourceDirectory = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } configured
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");
            foreach (var name in LinkedFiles)
            {
                var source = Path.Combine(sourceDirectory, name);
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Path.Combine(AgentDirectory, name);
                try
                {
                    File.CreateSymbolicLink(target, source);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    File.Copy(source, target);
                }
            }
        }

        public string AgentDirectory { get; }

        public string WorkingDirectory { get; }

        public void WriteExtension(string fileName, string source)
        {
            File.WriteAllText(Path.Combine(AgentDirectory, "extensions", fileName), source, new UTF8Encoding(false));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
