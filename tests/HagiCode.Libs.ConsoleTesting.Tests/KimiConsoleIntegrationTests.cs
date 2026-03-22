using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Kimi.Console;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Kimi;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class KimiConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Fact]
    public async Task DispatchAsync_runs_kimi_default_suite_with_fake_provider()
    {
        var provider = new FakeKimiProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new KimiConsoleRunner(KimiConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], KimiConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] kimi / Ping");
        rendered.ShouldContain("[PASS] kimi / Simple Prompt");
        rendered.ShouldContain("[PASS] kimi / Complex Prompt");
        rendered.ShouldContain("[PASS] kimi / Session Resume");
        rendered.ShouldContain("Summary: 4/4 passed");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeKimiProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new KimiConsoleRunner(KimiConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], KimiConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--model <model>");
        rendered.ShouldContain("--executable <path>");
        rendered.ShouldContain("--arg <value>");
        rendered.ShouldContain("--auth-method <id>");
    }

    [Fact]
    public async Task DispatchAsync_accepts_the_kimi_cli_alias()
    {
        var provider = new FakeKimiProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new KimiConsoleRunner(KimiConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "kimi-cli"], KimiConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("[PASS] kimi / Ping");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var provider = new FakeKimiProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new KimiConsoleRunner(KimiConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "codex"], KimiConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [Fact]
    public async Task DispatchAsync_reports_configuration_failures_for_invalid_kimi_options()
    {
        var provider = new FakeKimiProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new KimiConsoleRunner(KimiConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--executable"],
            KimiConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("--executable requires a value");
    }

    [Fact]
    public async Task DispatchAsync_passes_default_and_explicit_runtime_options_to_scenarios()
    {
        var provider = new FakeKimiProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new KimiConsoleRunner(KimiConsoleDefinition.Instance, provider, formatter);
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"kimi-console-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);

        try
        {
            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
                ["--test-provider-full", "--repo", repositoryPath, "--executable", "/tmp/kimi", "--arg", "--profile=smoke"],
                KimiConsoleDefinition.Instance,
                runner,
                output);

            exitCode.ShouldBe(0);
            provider.ReceivedOptions.Count.ShouldBe(5);
            provider.ReceivedOptions[0].ExecutablePath.ShouldBe("/tmp/kimi");
            provider.ReceivedOptions[0].ExtraArguments.ShouldBe(["--profile=smoke"]);
            provider.ReceivedOptions[3].SessionId.ShouldNotBeNullOrWhiteSpace();
            provider.ReceivedOptions[4].WorkingDirectory.ShouldBe(repositoryPath);
            output.ToString().ShouldContain("[PASS] kimi / Repository Summary");
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }

        provider = new FakeKimiProvider();
        using var explicitOutput = new StringWriter();
        formatter = new ProviderConsoleOutputFormatter(explicitOutput);
        runner = new KimiConsoleRunner(KimiConsoleDefinition.Instance, provider, formatter);

        var explicitExitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--model", "kimi-k2.5", "--auth-method", "token", "--auth-token", "secret"],
            KimiConsoleDefinition.Instance,
            runner,
            explicitOutput);

        explicitExitCode.ShouldBe(0);
        provider.ReceivedOptions[0].Model.ShouldBe("kimi-k2.5");
        provider.ReceivedOptions[0].AuthenticationMethod.ShouldBe("token");
        provider.ReceivedOptions[0].AuthenticationToken.ShouldBe("secret");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_ping_the_real_kimi_cli_when_opted_in()
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
            var exitCode = await Program.Main(["--test-provider"]);
            exitCode.ShouldBe(0);
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
            var exitCode = await Program.Main([]);
            exitCode.ShouldBe(0);
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

    private sealed class FakeKimiProvider : ICliProvider<KimiOptions>
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];

        public string Name => "kimi";

        public bool IsAvailable => true;

        public List<KimiOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "kimi-test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            KimiOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var sessionId = options.SessionId ?? $"session-{ReceivedOptions.Count}";
            var response = BuildResponse(prompt, options, sessionId);

            yield return new CliMessage(
                options.SessionId is null ? "session.started" : "session.resumed",
                JsonSerializer.SerializeToElement(new
                {
                    type = options.SessionId is null ? "session.started" : "session.resumed",
                    session_id = sessionId
                }));
            foreach (var chunk in SplitResponse(response))
            {
                yield return new CliMessage(
                    "assistant",
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "assistant",
                        session_id = sessionId,
                        text = chunk
                    }));
            }
            yield return new CliMessage(
                "terminal.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "terminal.completed",
                    session_id = sessionId,
                    stop_reason = "end_turn"
                }));
            await Task.Yield();
        }

        private string BuildResponse(string prompt, KimiOptions options, string sessionId)
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

            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            {
                var repositoryName = new DirectoryInfo(options.WorkingDirectory).Name;
                return $"Repository {repositoryName} contains src, tests, docs, and packages with .cs and .json files.";
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
