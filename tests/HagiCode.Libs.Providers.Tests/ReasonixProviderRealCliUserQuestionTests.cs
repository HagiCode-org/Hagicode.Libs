using System.Diagnostics;
using System.Text;
using System.Text.Json;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.Reasonix;
using Shouldly;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Contract tests of the Reasonix question path against the installed <c>reasonix</c> executable and a real model. They run
/// only when <c>HAGICODE_REAL_CLI_TESTS</c> is <c>1</c> or <c>true</c>, like the other Reasonix real-CLI tests; otherwise
/// they return before any process is started. <c>HAGICODE_REAL_CLI_REASONIX_MODEL</c> picks the model ref (for example
/// <c>omniroute/cc/claude-haiku-4-5-20251001</c>); without it Reasonix uses the default model of its configuration. The
/// working directory is a scratch directory; Reasonix reads its providers from the user's Reasonix home. Verified against
/// Reasonix v2.41.0. A failure message names the Reasonix behavior that changed.
/// </summary>
public sealed class ReasonixProviderRealCliUserQuestionTests
{
    private const string SwitchVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string ModelVariable = "HAGICODE_REAL_CLI_REASONIX_MODEL";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(240);

    private const string AskPrompt =
        "Use the ask tool exactly once. Ask one question with header \"Color\", question \"Which color should I use?\", options \"Red\" (description \"Warm color\") and \"Blue\" (description \"Cool color\"), and multiSelect false. After I answer, reply with only the color I picked. Do not use any other tool.";

    [Test]
    [Category("RealCli")]
    public async Task A_question_is_asked_answered_with_a_label_and_the_agent_continues_with_the_answer()
    {
        if (!IsEnabled())
        {
            return;
        }

        var requests = new List<ReasonixUserInputRequest>();
        var run = await RunAsync(new ReasonixOptions
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult<ReasonixUserInputResponse?>(new ReasonixUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [request.Question.Question] = ["Blue"] }));
            }
        });

        var request = requests.ShouldHaveSingleItem("Reasonix did not send exactly one recognized ask-<turn>-qN question request");
        request.RequestId.ShouldMatch(@"^ask-.*-\d+-q1$", "the question tool call id form changed");
        request.OptionIdsByLabel.Values.ShouldBe(["q1:1", "q1:2"], customMessage: "the question option id form changed");
        request.CancelOptionId.ShouldBe("q1:cancel", "the question cancel option changed");
        request.Question.Options.Select(static option => option.Label).ShouldBe(["Red", "Blue"]);
        run.Messages.Count(static message => message.Type == ReasonixUserInputRequest.MessageType).ShouldBe(1);
        run.Messages[^1].Type.ShouldBe("terminal.completed", run.Describe());
        run.Messages.ShouldNotContain(static message => message.Type == "terminal.failed");
        run.AssistantText.ShouldContain("Blue", Case.Insensitive);
        run.AssertNoReasonixProcessLeft();
    }

    [Test]
    [Category("RealCli")]
    public async Task An_unanswered_question_times_out_and_the_run_ends_as_a_completion_without_a_provider_error()
    {
        if (!IsEnabled())
        {
            return;
        }

        CancellationToken handlerToken = default;
        var run = await RunAsync(new ReasonixOptions
        {
            QuestionTimeout = TimeSpan.FromSeconds(3),
            UserInputHandler = async (_, token) =>
            {
                handlerToken = token;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
        });

        handlerToken.IsCancellationRequested.ShouldBeTrue("the handler was not called or not cancelled by the question timeout");
        var terminal = run.Messages[^1];
        terminal.Type.ShouldBe("terminal.completed", run.Describe());
        run.Messages.ShouldNotContain(static message => message.Type == "terminal.failed");
        terminal.Content.TryGetProperty("question_declined", out var declined).ShouldBeTrue("the run did not end after the declined question");
        declined.GetBoolean().ShouldBeTrue();
        terminal.Content.GetProperty("stop_reason").GetString().ShouldBe("error", "the stop reason after a declined question changed");
        run.AssertNoReasonixProcessLeft();
    }

    // ---------------------------------------------------------------- helpers

    private async Task<RunResult> RunAsync(ReasonixOptions options)
    {
        using var workspace = new ScratchDirectory();
        var model = Environment.GetEnvironmentVariable(ModelVariable);
        var executable = new CliExecutableResolver().ResolveFirstAvailablePath(["reasonix"])
                         ?? throw new InvalidOperationException("The real CLI lane needs a reasonix executable on PATH.");
        TestContext.Out.WriteLine($"reasonix executable: {executable}");
        TestContext.Out.WriteLine($"reasonix version: {await ReadVersionAsync(executable)}");
        TestContext.Out.WriteLine($"model: {(string.IsNullOrWhiteSpace(model) ? "(reasonix default)" : model)}");

        var manager = new RecordingProcessManager();
        var provider = new ReasonixProvider(new CliExecutableResolver(), manager);
        using var cancellation = new CancellationTokenSource(RunTimeout);
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(
                           options with
                           {
                               ExecutablePath = executable,
                               WorkingDirectory = workspace.Path,
                               Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                               StartupTimeout = TimeSpan.FromSeconds(30)
                           },
                           AskPrompt,
                           cancellation.Token))
        {
            messages.Add(message);
        }

        var result = new RunResult(messages, manager);
        TestContext.Out.WriteLine(result.Describe());
        return result;
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

    private sealed class RunResult(IReadOnlyList<CliMessage> messages, RecordingProcessManager manager)
    {
        public IReadOnlyList<CliMessage> Messages { get; } = messages;

        public string AssistantText => string.Concat(Messages
            .Where(static message => message.Type == "assistant")
            .Select(static message => message.Content.TryGetProperty("text", out var text) ? text.GetString() : null));

        public void AssertNoReasonixProcessLeft()
        {
            manager.Pids.ShouldNotBeEmpty();
            foreach (var pid in manager.Pids)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (ProcessIsRunning(pid) && DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(100);
                }

                ProcessIsRunning(pid).ShouldBeFalse($"reasonix process {pid} is still running");
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
                if (message.Content.TryGetProperty("stop_reason", out var stopReason) && stopReason.ValueKind == JsonValueKind.String)
                {
                    builder.Append(" [").Append(stopReason.GetString()).Append(']');
                }

                if (text is { Length: > 0 })
                {
                    builder.Append(": ").Append(text.Length > 160 ? text[..160] + "..." : text);
                }

                builder.AppendLine();
            }

            builder.Append("pids: ").Append(string.Join(",", manager.Pids));
            return builder.ToString();
        }
    }

    /// <summary>Starts processes like the default manager and remembers their ids.</summary>
    private sealed class RecordingProcessManager : CliProcessManager
    {
        public List<int> Pids { get; } = [];

        public override async ValueTask<CliProcessHandle> StartAsync(ProcessStartContext context, CancellationToken cancellationToken = default)
        {
            var handle = await base.StartAsync(context, cancellationToken);
            Pids.Add(handle.Process.Id);
            return handle;
        }
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-reasonix-question-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
