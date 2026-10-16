using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shouldly;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Omp;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Contract tests of the interactive question path against a real <c>omp</c> executable and a real model. They spend a few
/// model calls each, so they run only when <c>HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION</c> is <c>1</c> or <c>true</c>.
/// <c>HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION_MODEL</c> picks the model selector; without it omp uses the default model of its
/// configuration. <c>HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION_EXECUTABLE</c> picks a specific executable, otherwise <c>omp</c>
/// is resolved from <c>PATH</c>. The omp version is written to the test output. Every run gets an isolated home whose agent
/// directory only links the <c>models.yml</c> and <c>config.yml</c> of the current user's omp agent directory
/// (<c>PI_CODING_AGENT_DIR</c>, or <c>~/.omp/agent</c>), so the runs never touch the user's extensions, sessions or logs: no
/// session is persisted and the working directory is a scratch directory. Verified against omp 18.10.4.
/// </summary>
[Category("Integration")]
public sealed class OmpProviderRealCliUserQuestionTests
{
    private const string SwitchVariable = "HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION";
    private const string ExecutableVariable = "HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION_EXECUTABLE";
    private const string ModelVariable = "HAGICODE_LIBS_REAL_CLI_OMP_USER_QUESTION_MODEL";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(180);

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Two_questions_one_of_them_multi_select_are_asked_answered_and_the_agent_receives_the_answers()
    {
        ExternalGate.Require(SwitchVariable);

        using var home = new IsolatedOmpHome();
        var requests = new List<OmpUserInputRequest>();
        var options = CreateOptions(home) with
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<OmpUserInputResponse?>(new OmpUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
                {
                    [request.Questions[0].Question] = ["Blue"],
                    [request.Questions[1].Question] = ["Dark theme", "Offline mode"]
                }));
            }
        };

        var run = await RunAsync(
            options,
            "Call the ask tool exactly once, before anything else, with exactly these two questions. Question 1: id \"colour\", header \"Colour\", question \"Which colour do you prefer?\", single choice, options Red (description \"Warm\") and Blue (description \"Cool\"). Question 2: id \"features\", header \"Features\", question \"Which features do you want?\", multi true, options \"Dark theme\" (description \"Dark colours\"), \"Mobile layout\" (description \"Small screens\"), \"Offline mode\" (description \"No network\"). After I answer, reply with exactly: ANSWER=<colour>|<features>. Do not use any other tool.");

        var request = requests.ShouldHaveSingleItem();
        request.Questions.Select(static question => question.Id).ShouldBe(["colour", "features"]);
        request.Questions[0].MultiSelect.ShouldBeFalse();
        request.Questions[0].Header.ShouldBe("Colour");
        request.Questions[0].Options.Select(static option => option.Label).ShouldBe(["Red", "Blue"]);
        request.Questions[1].MultiSelect.ShouldBeTrue();
        request.Questions[1].Options.Select(static option => option.Label).ShouldBe(["Dark theme", "Mobile layout", "Offline mode"]);
        request.Questions[1].Options.ShouldAllBe(static option => option.Description.Length > 0);

        run.Completed.ShouldBeTrue(run.Describe());
        var types = run.Messages.Select(static message => message.Type).ToList();
        types[0].ShouldBe("session.started");
        types.IndexOf("tool.call").ShouldBeLessThan(types.IndexOf(OmpUserInputRequest.MessageType));
        types.IndexOf(OmpUserInputRequest.MessageType).ShouldBeLessThan(types.IndexOf("tool.completed"));
        run.ToolResult(OmpUserInputRequest.QuestionToolName).ShouldContain("colour: Blue");
        run.ToolResult(OmpUserInputRequest.QuestionToolName).ShouldContain("features: [Dark theme, Offline mode]");
        run.AssistantText.ShouldContain("Blue", Case.Sensitive);
        run.AssistantText.ShouldContain("Offline mode", Case.Sensitive);
        run.AssertNoOmpProcessLeft();
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task A_question_answered_after_more_than_sixty_seconds_continues_the_run()
    {
        ExternalGate.Require(SwitchVariable);

        using var home = new IsolatedOmpHome();
        var asked = Stopwatch.StartNew();
        TimeSpan answeredAfter = default;
        var options = CreateOptions(home) with
        {
            UserInputHandler = async (request, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(65), token);
                answeredAfter = asked.Elapsed;
                var question = request.Questions.Single();
                return new OmpUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [question.Question] = [question.Options[^1].Label] });
            }
        };

        var run = await RunAsync(
            options,
            "Use the ask tool exactly once to ask me ONE single-choice question: which colour do I prefer, options Red and Blue (Red first). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool.",
            timeout: TimeSpan.FromSeconds(240));

        answeredAfter.ShouldBeGreaterThan(TimeSpan.FromSeconds(60));
        run.Completed.ShouldBeTrue(run.Describe());
        run.AssistantText.ShouldContain("Blue", Case.Insensitive);
        run.AssertNoOmpProcessLeft();
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Single_multi_and_free_text_answers_and_a_label_with_a_comma_reach_the_model_without_a_tool_error()
    {
        ExternalGate.Require(SwitchVariable);

        using var home = new IsolatedOmpHome();
        var requests = new List<OmpUserInputRequest>();
        var options = CreateOptions(home) with
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<OmpUserInputResponse?>(new OmpUserInputResponse(new Dictionary<string, IReadOnlyList<string>>
                {
                    [request.Questions[0].Question] = ["Blue"],
                    [request.Questions[1].Question] = ["Red, dark", "Green"],
                    [request.Questions[2].Question] = ["Teal, actually"]
                }));
            }
        };

        var run = await RunAsync(
            options,
            "Call the ask tool exactly once, before anything else, with exactly these three questions. Question 1: id \"one\", question \"Pick one colour\", single choice, options \"Red\" and \"Blue\". Question 2: id \"many\", question \"Pick colours\", multi true, options \"Red, dark\", \"Blue\", \"Green\" (the first label contains a comma; keep it exactly). Question 3: id \"free\", question \"Any other colour?\", single choice, options \"None\" and \"Purple\". After I answer, reply with exactly the answers you received, one per line. Do not use any other tool.");

        run.Completed.ShouldBeTrue(run.Describe());
        var request = requests.ShouldHaveSingleItem();
        request.Questions[1].Options.Select(static option => option.Label).ShouldContain("Red, dark");
        run.Messages.ShouldNotContain(static message => message.Type == "tool.failed");
        var result = run.ToolResult(OmpUserInputRequest.QuestionToolName);
        result.ShouldContain("one: Blue");
        result.ShouldContain("many: [Red, dark, Green]");
        result.ShouldContain("free: \"Teal, actually\"");
        run.AssertNoOmpProcessLeft();
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task A_declined_question_ends_the_run_as_aborted_without_an_error()
    {
        ExternalGate.Require(SwitchVariable);

        using var home = new IsolatedOmpHome();
        var handlerCalls = 0;
        var options = CreateOptions(home) with
        {
            UserInputHandler = (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<OmpUserInputResponse?>(null);
            }
        };

        var run = await RunAsync(
            options,
            "Use the ask tool exactly once to ask me which colour I prefer: Red or Blue. After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool.");

        handlerCalls.ShouldBe(1);
        run.Completed.ShouldBeTrue(run.Describe());
        run.Messages[^1].Content.GetProperty("stop_reason").GetString().ShouldBe("aborted");
        run.Messages.ShouldNotContain(static message => message.Type == "terminal.failed");
        run.ToolResult(OmpUserInputRequest.QuestionToolName, "tool.failed").ShouldContain("cancelled by the user");
        run.AssertNoOmpProcessLeft();
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task Cancelling_while_a_question_is_pending_ends_the_run_and_leaves_no_omp_process()
    {
        ExternalGate.Require(SwitchVariable);

        using var home = new IsolatedOmpHome();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken handlerToken = default;
        var options = CreateOptions(home) with
        {
            UserInputHandler = (_, token) =>
            {
                handlerToken = token;
                handlerStarted.TrySetResult();
                return new TaskCompletionSource<OmpUserInputResponse?>().Task;
            }
        };
        var manager = new RecordingProcessManager();
        var provider = new OmpProvider(new CliExecutableResolver(), manager);
        using var cancellation = new CancellationTokenSource(RunTimeout);
        var messages = new List<CliMessage>();
        var cancelled = Stopwatch.StartNew();

        var act = async () =>
        {
            await foreach (var message in provider.ExecuteAsync(
                               options,
                               "Use the ask tool exactly once to ask me which colour I prefer: Red or Blue. Then reply with exactly: DONE.",
                               cancellation.Token))
            {
                messages.Add(message);
                if (message.Type == OmpUserInputRequest.MessageType)
                {
                    await handlerStarted.Task;
                    await Task.Delay(500);
                    cancelled.Restart();
                    await cancellation.CancelAsync();
                }
            }
        };

        var exception = Assert.CatchAsync<OperationCanceledException>(act);
        TestContext.Out.WriteLine($"cancel took {cancelled.Elapsed.TotalMilliseconds:F0} ms; omp process stops requested: {manager.StopCalls}; pids: {string.Join(",", manager.Pids)}");

        exception.ShouldBeAssignableTo<OperationCanceledException>();
        handlerToken.IsCancellationRequested.ShouldBeTrue();
        messages.ShouldContain(static message => message.Type == OmpUserInputRequest.MessageType);
        manager.Pids.ShouldHaveSingleItem();
        ProcessIsRunning(manager.Pids[0]).ShouldBeFalse("the omp process must be gone when ExecuteAsync returns");
        cancelled.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task A_user_extension_in_the_omp_home_still_works_its_dialogs_are_cancelled_and_questions_are_still_asked()
    {
        ExternalGate.Require(SwitchVariable);

        using var home = new IsolatedOmpHome();
        home.WriteExtension("user-dialogs.ts", UserDialogsExtension);
        var requests = new List<OmpUserInputRequest>();
        var options = CreateOptions(home) with
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                var question = request.Questions.Single();
                return Task.FromResult<OmpUserInputResponse?>(new OmpUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [question.Question] = [question.Options[^1].Label] }));
            }
        };

        var run = await RunAsync(
            options,
            "Step 1: call the user_dialogs tool once and read its result. Step 2: use the ask tool exactly once to ask me which colour I prefer: Red or Blue (single choice, Red first). Step 3: reply with exactly: DONE <the user_dialogs result> answer=<my choice>. Do not use any other tool.");

        run.Completed.ShouldBeTrue(run.Describe());

        // The user extension's dialogs were dismissed, never offered to the handler; the question still arrived.
        requests.ShouldHaveSingleItem();
        run.Messages.Count(static message => message.Type == OmpUserInputRequest.MessageType).ShouldBe(1);
        run.AssistantText.ShouldContain("select=undefined", Case.Insensitive);
        run.AssistantText.ShouldContain("confirm=false", Case.Insensitive);
        run.AssistantText.ShouldContain("Blue", Case.Insensitive);
        run.AssertNoOmpProcessLeft();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<RunResult> RunAsync(OmpOptions options, string prompt, TimeSpan? timeout = null)
    {
        var executable = options.ExecutablePath!;
        TestContext.Out.WriteLine($"omp executable: {executable}");
        TestContext.Out.WriteLine($"omp version: {await ReadVersionAsync(executable)}");
        TestContext.Out.WriteLine($"model: {options.Model ?? "(omp default)"}");

        var manager = new RecordingProcessManager();
        var provider = new OmpProvider(new CliExecutableResolver(), manager);
        using var cancellation = new CancellationTokenSource(timeout ?? RunTimeout);
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellation.Token))
        {
            messages.Add(message);
        }

        var result = new RunResult(messages, manager);
        TestContext.Out.WriteLine(result.Describe());
        return result;
    }

    private static OmpOptions CreateOptions(IsolatedOmpHome home)
    {
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        return new OmpOptions
        {
            ExecutablePath = ResolveExecutable(),
            WorkingDirectory = home.WorkingDirectory,
            Model = string.IsNullOrWhiteSpace(model) ? null : model,
            NoSession = true,
            QuestionTimeout = TimeSpan.FromMinutes(5),
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["HOME"] = home.HomeDirectory,
                ["PI_CODING_AGENT_DIR"] = home.AgentDirectory
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

        return new CliExecutableResolver().ResolveFirstAvailablePath(["omp"])
               ?? throw new InvalidOperationException("The real CLI lane needs an omp executable on PATH.");
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

    private static bool ProcessIsRunning(int processId)
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
        import type { ExtensionAPI } from "@oh-my-pi/pi-coding-agent";

        // A user extension that opens dialogs of its own during a run. Under rpc-ui they reach the host.
        export default function (pi: ExtensionAPI) {
          const z = pi.zod;
          pi.registerTool({
            name: "user_dialogs",
            label: "User dialogs",
            description: "Opens a few dialogs and reports what came back.",
            parameters: z.object({}),
            async execute(_id, _params, _signal, _onUpdate, ctx) {
              ctx.ui.notify("hello from a user extension", "info");
              ctx.ui.setStatus("user-dialogs", "working");
              const selected = await ctx.ui.select("Pick one", ["a", "b"]);
              const confirmed = await ctx.ui.confirm("Sure?", "really");
              const typed = await ctx.ui.input("Free text", "type something");
              const edited = await ctx.ui.editor("Edit", "draft");
              return { content: [{ type: "text", text: `select=${selected} confirm=${confirmed} input=${typed} editor=${edited}` }], details: {} };
            },
          });
        }
        """;

    private sealed class RunResult(IReadOnlyList<CliMessage> messages, RecordingProcessManager manager)
    {
        public IReadOnlyList<CliMessage> Messages { get; } = messages;

        public bool Completed => Messages.Count > 0 && Messages[^1].Type == "terminal.completed";

        public string AssistantText => string.Concat(Messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.TryGetProperty("text", out var text) ? text.GetString() : null));

        public string ToolResult(string toolName, string messageType = "tool.completed")
        {
            return Messages
                .Where(message => message.Type == messageType
                                  && message.Content.TryGetProperty("tool_name", out var name)
                                  && name.GetString() == toolName)
                .Select(static message => message.Content.TryGetProperty("text", out var text) ? text.GetString() : null)
                .FirstOrDefault() ?? string.Empty;
        }

        public void AssertNoOmpProcessLeft()
        {
            manager.Pids.ShouldNotBeEmpty();
            foreach (var pid in manager.Pids)
            {
                ProcessIsRunning(pid).ShouldBeFalse($"omp process {pid} is still running");
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

    /// <summary>
    /// A home of its own for omp: its agent directory links the model and configuration files of the current user's omp agent
    /// directory and has a scratch <c>extensions</c> folder, so a test can plant user extensions without touching the real
    /// home, and omp's logs and caches land in the scratch home as well.
    /// </summary>
    private sealed class IsolatedOmpHome : IDisposable
    {
        private static readonly string[] LinkedFiles = ["models.yml", "config.yml"];

        private readonly string _root;

        public IsolatedOmpHome()
        {
            _root = Path.Combine(Path.GetTempPath(), $"hagicode-libs-omp-real-{Guid.NewGuid():N}");
            HomeDirectory = Path.Combine(_root, "home");
            AgentDirectory = Path.Combine(HomeDirectory, ".omp", "agent");
            WorkingDirectory = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(Path.Combine(AgentDirectory, "extensions"));
            Directory.CreateDirectory(WorkingDirectory);

            var sourceDirectory = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } configured
                ? configured
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omp", "agent");
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

            // omp unpacks about 200 MB of native modules into ~/.omp/natives on first use; reuse the ones already unpacked.
            var natives = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourceDirectory))!, "natives");
            if (Directory.Exists(natives))
            {
                try
                {
                    Directory.CreateSymbolicLink(Path.Combine(HomeDirectory, ".omp", "natives"), natives);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        public string HomeDirectory { get; }

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
                // Remove the link itself first, so nothing can reach the shared natives through it.
                var natives = Path.Combine(HomeDirectory, ".omp", "natives");
                if (new FileInfo(natives).LinkTarget is not null)
                {
                    File.Delete(natives);
                }

                Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Leave the scratch home behind rather than risk anything shared.
            }
        }
    }
}
