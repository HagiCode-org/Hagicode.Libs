using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit.Abstractions;
using HagiCode.Libs.Core.Discovery;
using HagiCode.Libs.Core.Process;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.Providers.Tests;

/// <summary>
/// Contract tests of the interactive question protocol against a real <c>claude</c> executable. They spend a few cents of
/// model usage each, so they run only when <c>HAGICODE_LIBS_REAL_CLI_USER_QUESTION</c> is <c>1</c> or <c>true</c>.
/// <c>HAGICODE_LIBS_REAL_CLI_USER_QUESTION_EXECUTABLE</c> picks a specific executable, which is how each supported CLI version
/// is checked; otherwise <c>claude</c> is resolved from <c>PATH</c>. The CLI version is written to the test output.
/// Every run uses a scratch working directory and the <c>haiku</c> model.
/// </summary>
public sealed class ClaudeCodeProviderRealCliUserQuestionTests(ITestOutputHelper output)
{
    private const string SwitchVariable = "HAGICODE_LIBS_REAL_CLI_USER_QUESTION";
    private const string ExecutableVariable = "HAGICODE_LIBS_REAL_CLI_USER_QUESTION_EXECUTABLE";
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task A_question_is_asked_answered_and_the_turn_continues_with_the_answer()
    {
        if (!IsEnabled())
        {
            return;
        }

        using var workspace = new ScratchDirectory();
        var executable = ResolveExecutable();
        var requests = new List<ClaudeUserInputRequest>();
        var options = CreateOptions(executable, workspace.Path) with
        {
            UserInputHandler = (request, _) =>
            {
                requests.Add(request);
                var question = request.Questions.Single();
                return Task.FromResult<ClaudeUserInputResponse?>(new ClaudeUserInputResponse(
                    new Dictionary<string, IReadOnlyList<string>> { [question.Question] = [question.Options[1].Label] }));
            }
        };

        var messages = await RunAsync(
            options,
            "Use the AskUserQuestion tool to ask me which colour I prefer: red or blue (2 options). After I answer, reply with exactly: ANSWER=<my choice>. Do not use any other tool.");

        var request = requests.ShouldHaveSingleItem();
        var chosen = request.Questions.Single().Options[1].Label;
        var types = messages.Select(static message => message.Type).ToList();
        types.ShouldContain("control_request");
        types.ShouldNotContain("control_response");
        types.IndexOf("control_request").ShouldBeGreaterThan(types.IndexOf("assistant"));

        var result = messages.Last(static message => message.Type == "result").Content;
        result.GetProperty("is_error").GetBoolean().ShouldBeFalse();
        result.GetProperty("result").GetString().ShouldContain(chosen, Case.Insensitive);
        result.GetProperty("permission_denials").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task A_tool_permission_request_gets_the_fixed_deny_without_asking_the_handler()
    {
        if (!IsEnabled())
        {
            return;
        }

        using var workspace = new ScratchDirectory();
        var executable = ResolveExecutable();
        var handlerCalls = 0;
        var options = CreateOptions(executable, workspace.Path) with
        {
            UserInputHandler = (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return Task.FromResult<ClaudeUserInputResponse?>(null);
            }
        };

        var messages = await RunAsync(
            options,
            "Create a file named hello.txt containing the text hi using the Write tool exactly once, then reply with exactly: DONE. Do not use any other tool.");

        handlerCalls.ShouldBe(0);
        messages.ShouldNotContain(static message => message.Type == "control_request");

        // The denied call is what the model sees as the tool result, with the fixed message.
        var toolResults = messages
            .Where(static message => message.Type == "user"
                                     && message.Content.TryGetProperty("message", out var body)
                                     && body.TryGetProperty("content", out var content)
                                     && content.ValueKind == JsonValueKind.Array)
            .SelectMany(static message => message.Content.GetProperty("message").GetProperty("content").EnumerateArray())
            .Where(static block => block.TryGetProperty("type", out var type) && type.GetString() == "tool_result")
            .ToList();
        var denied = toolResults.ShouldHaveSingleItem();
        denied.GetProperty("is_error").GetBoolean().ShouldBeTrue();
        denied.GetProperty("content").GetString().ShouldBe(ClaudeControlProtocol.FixedDenyMessage);

        var result = messages.Last(static message => message.Type == "result").Content;
        result.GetProperty("is_error").GetBoolean().ShouldBeFalse();
        result.GetProperty("permission_denials").EnumerateArray()
            .Select(static denial => denial.GetProperty("tool_name").GetString())
            .ShouldBe(["Write"]);
        File.Exists(Path.Combine(workspace.Path, "hello.txt")).ShouldBeFalse();
    }

    private async Task<List<CliMessage>> RunAsync(ClaudeCodeOptions options, string prompt)
    {
        output.WriteLine($"claude executable: {options.ExecutablePath}");
        output.WriteLine($"claude version: {await ReadVersionAsync(options.ExecutablePath!)}");

        await using var provider = new ClaudeCodeProvider(new CliExecutableResolver(), new CliProcessManager());
        using var cancellation = new CancellationTokenSource(RunTimeout);
        var messages = new List<CliMessage>();
        await foreach (var message in provider.ExecuteAsync(options, prompt, cancellation.Token))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static ClaudeCodeOptions CreateOptions(string executable, string workingDirectory)
    {
        return new ClaudeCodeOptions
        {
            ExecutablePath = executable,
            WorkingDirectory = workingDirectory,
            Model = "haiku",
            MaxTurns = 6
        };
    }

    private static string ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return new CliExecutableResolver().ResolveFirstAvailablePath(["claude"])
               ?? throw new InvalidOperationException("The real CLI lane needs a claude executable on PATH.");
    }

    private static async Task<string> ReadVersionAsync(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false
        })!;
        var version = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return version.Trim();
    }

    private static bool IsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(SwitchVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ScratchDirectory : IDisposable
    {
        public ScratchDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hagicode-libs-question-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

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
