using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Dsh.Console;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Dsh;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class DshConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";
    private const string TestProvider = "omniroute";
    private const string TestModel = "claude/claude-haiku-5-5";

    [Fact]
    public async Task DispatchAsync_runs_dsh_default_suite_with_fake_provider()
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], DshConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] dsh / Ping");
        rendered.ShouldContain("[PASS] dsh / Simple Prompt");
        rendered.ShouldContain("[PASS] dsh / Complex Prompt");
        rendered.ShouldContain("[PASS] dsh / Session Resume");
        rendered.ShouldContain("Summary: 4/4 passed");
        rendered.ShouldNotContain("Model:");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], DshConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--executable <path>");
        rendered.ShouldContain("--provider <id>");
        rendered.ShouldContain("--model <model>");
        rendered.ShouldContain("--arg <value>");
        rendered.ShouldContain("--workspace <path>");
        rendered.ShouldContain("--dsh-home <path>");
        rendered.ShouldContain("--verbose");
    }

    [Fact]
    public async Task DispatchAsync_accepts_the_dsh_cli_alias()
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "dsh-cli"], DshConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("[PASS] dsh / Ping");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "codex"], DshConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [Theory]
    [InlineData("--executable", "--executable requires a value")]
    [InlineData("--bogus", "Unknown option: --bogus")]
    public async Task DispatchAsync_reports_configuration_failures_for_invalid_dsh_options(string option, string expectedMessage)
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", option],
            DshConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain(expectedMessage);
        provider.ReceivedOptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_forwards_no_default_model_or_environment_when_flags_are_absent()
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider-full"], DshConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        provider.ReceivedOptions.Count.ShouldBe(4);
        foreach (var options in provider.ReceivedOptions)
        {
            options.Provider.ShouldBeNull();
            options.Model.ShouldBeNull();
            options.ExecutablePath.ShouldBeNull();
            options.WorkingDirectory.ShouldBeNull();
            options.EnvironmentVariables.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task DispatchAsync_passes_explicit_runtime_options_and_resumes_the_returned_session()
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));
        const string workspacePath = "/tmp/dsh-console-workspace";
        const string dshHomePath = "/tmp/dsh-console-home";

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            [
                "--test-provider-full",
                "--executable", "/tmp/dsh",
                "--provider", TestProvider,
                "--model", TestModel,
                "--arg", "--trace",
                "--workspace", workspacePath,
                "--dsh-home", dshHomePath
            ],
            DshConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(0);
        provider.ReceivedOptions.Count.ShouldBe(4);
        foreach (var options in provider.ReceivedOptions)
        {
            options.ExecutablePath.ShouldBe("/tmp/dsh");
            options.Provider.ShouldBe(TestProvider);
            options.Model.ShouldBe(TestModel);
            options.WorkingDirectory.ShouldBe(workspacePath);
            options.ExtraArguments.ShouldBe(["--trace"]);
            options.EnvironmentVariables["DSH_HOME"].ShouldBe(dshHomePath);
        }

        provider.ReceivedOptions[3].SessionId.ShouldBe(provider.EmittedSessionIds[2]);
        output.ToString().ShouldContain("Summary: 4/4 passed");
    }

    [Fact]
    public async Task DispatchAsync_verbose_adds_model_and_workspace_details_to_scenarios()
    {
        var provider = new FakeDshProvider();
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--provider", TestProvider, "--model", TestModel, "--workspace", "/tmp/dsh-console-workspace", "--verbose"],
            DshConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain($"Provider: {TestProvider}");
        rendered.ShouldContain($"Model: {TestModel}");
        rendered.ShouldContain("Workspace: /tmp/dsh-console-workspace");
    }

    [Fact]
    public async Task DispatchAsync_session_resume_fails_when_dsh_exposes_no_session_id()
    {
        var provider = new FakeDshProvider(omitSessionId: true);
        using var output = new StringWriter();
        var runner = new DshConsoleRunner(DshConsoleDefinition.Instance, provider, new ProviderConsoleOutputFormatter(output));

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], DshConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        var rendered = output.ToString();
        rendered.ShouldContain("[FAIL] dsh / Session Resume");
        rendered.ShouldContain("dsh did not expose a session identifier");
        rendered.ShouldContain("Summary: 3/4 passed");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_ping_the_real_dsh_cli_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        using var output = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(output);
            var exitCode = await Program.Main(["--test-provider", "--provider", TestProvider, "--model", TestModel]);
            exitCode.ShouldBe(0);
            output.ToString().ShouldContain("[PASS] dsh / Ping");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_run_the_full_default_suite_when_opted_in()
    {
        if (!IsRealCliTestsEnabled())
        {
            return;
        }

        using var output = new StringWriter();
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(output);
            var exitCode = await Program.Main(["--test-provider-full", "--provider", TestProvider, "--model", TestModel]);
            exitCode.ShouldBe(0);
            var rendered = output.ToString();
            rendered.ShouldContain("[PASS] dsh / Session Resume");
            rendered.ShouldContain("Summary: 4/4 passed");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeDshProvider(bool omitSessionId = false) : ICliProvider<DshOptions>
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];

        public string Name => "dsh";

        public bool IsAvailable => true;

        public List<DshOptions> ReceivedOptions { get; } = [];

        public List<string> EmittedSessionIds { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "0.2.0-rc.2"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            DshOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var sessionId = options.SessionId ?? $"session-{ReceivedOptions.Count}";
            EmittedSessionIds.Add(sessionId);
            var lifecycleType = options.SessionId is null ? "session.started" : "session.resumed";

            yield return new CliMessage(lifecycleType, Payload(lifecycleType, sessionId));
            foreach (var chunk in SplitResponse(BuildResponse(prompt, sessionId)))
            {
                yield return new CliMessage("assistant", Payload("assistant", sessionId, text: chunk));
            }

            yield return new CliMessage("terminal.completed", Payload("terminal.completed", sessionId, stopReason: "end_turn"));
            await Task.Yield();
        }

        private JsonElement Payload(string type, string sessionId, string? text = null, string? stopReason = null)
        {
            var fields = new Dictionary<string, object?> { ["type"] = type };
            if (!omitSessionId)
            {
                fields["session_id"] = sessionId;
            }

            if (text is not null)
            {
                fields["text"] = text;
            }

            if (stopReason is not null)
            {
                fields["stop_reason"] = stopReason;
            }

            return JsonSerializer.SerializeToElement(fields);
        }

        private string BuildResponse(string prompt, string sessionId)
        {
            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var secret = ExtractSecret(prompt);
                if (secret is not null)
                {
                    _sessionSecrets[sessionId] = secret;
                }

                return "ACK";
            }

            if (prompt.Contains("What was the secret word I told you earlier", StringComparison.OrdinalIgnoreCase))
            {
                return _sessionSecrets.TryGetValue(sessionId, out var secret)
                    ? secret
                    : "UNKNOWN";
            }

            if (prompt.Contains("exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("software testing", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: faster feedback and safer refactors.\n- Trade-off: extra maintenance, flaky cases, and longer pipelines.";
            }

            return "pong";
        }

        private static string? ExtractSecret(string prompt)
        {
            const string marker = "Remember the secret word:";
            var markerIndex = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0)
            {
                return null;
            }

            var secretSegment = prompt[(markerIndex + marker.Length)..];
            var stopIndex = secretSegment.IndexOf('.', StringComparison.Ordinal);
            var value = stopIndex >= 0 ? secretSegment[..stopIndex] : secretSegment;
            return value.Trim();
        }

        private static IEnumerable<string> SplitResponse(string response)
        {
            const int chunkSize = 6;
            for (var index = 0; index < response.Length; index += chunkSize)
            {
                yield return response.Substring(index, Math.Min(chunkSize, response.Length - index));
            }
        }
    }
}
