using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Hermes.Console;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Hermes;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class HermesConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Fact]
    public async Task DispatchAsync_runs_hermes_default_suite_with_fake_provider()
    {
        var provider = new FakeHermesProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new HermesConsoleRunner(HermesConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], HermesConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] hermes / Ping");
        rendered.ShouldContain("[PASS] hermes / Simple Prompt");
        rendered.ShouldContain("[PASS] hermes / Complex Prompt");
        rendered.ShouldContain("[PASS] hermes / Memory Reuse");
        rendered.ShouldContain("Summary: 4/4 passed");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeHermesProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new HermesConsoleRunner(HermesConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], HermesConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--model <model>");
        rendered.ShouldContain("--arguments <value>");
    }

    [Fact]
    public async Task DispatchAsync_normalizes_hermes_aliases_for_provider_commands()
    {
        var provider = new FakeHermesProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new HermesConsoleRunner(HermesConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "hermes-cli"], HermesConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("[PASS] hermes / Ping");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var provider = new FakeHermesProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new HermesConsoleRunner(HermesConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "codex"], HermesConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [Fact]
    public async Task DispatchAsync_reports_configuration_failures_for_invalid_hermes_options()
    {
        var provider = new FakeHermesProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new HermesConsoleRunner(HermesConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--arguments", "\"unterminated"],
            HermesConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("unterminated quoted value");
    }

    [Fact]
    public async Task DispatchAsync_passes_hermes_specific_runtime_options_to_scenarios()
    {
        var provider = new FakeHermesProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new HermesConsoleRunner(HermesConsoleDefinition.Instance, provider, formatter);
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"hermes-console-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);

        try
        {
            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
                [
                    "--test-provider-full",
                    "--model", "hermes/default",
                    "--executable", "/custom/hermes",
                    "--arguments", "acp --profile smoke",
                    "--repo", repositoryPath
                ],
                HermesConsoleDefinition.Instance,
                runner,
                output);

            exitCode.ShouldBe(0);
            provider.ReceivedOptions.Count.ShouldBe(5);
            provider.ReceivedOptions[0].Model.ShouldBe("hermes/default");
            provider.ReceivedOptions[0].ExecutablePath.ShouldBe("/custom/hermes");
            provider.ReceivedOptions[0].Arguments.ShouldBe(["acp", "--profile", "smoke"]);
            provider.ReceivedOptions[3].SessionId.ShouldNotBeNullOrWhiteSpace();
            provider.ReceivedOptions[4].WorkingDirectory.ShouldBe(repositoryPath);
            output.ToString().ShouldContain("[PASS] hermes / Repository Summary");
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_ping_the_real_hermes_cli_when_opted_in()
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

    private static bool IsRealCliTestsEnabled()
    {
        var value = Environment.GetEnvironmentVariable(RealCliTestsEnvironmentVariable);
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeHermesProvider : ICliProvider<HermesOptions>
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];

        public string Name => "hermes";

        public bool IsAvailable => true;

        public List<HermesOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "hermes-test-0.4.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            HermesOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var sessionId = options.SessionId ?? $"session-{ReceivedOptions.Count}";
            var response = BuildResponse(prompt, options, sessionId);

            yield return new CliMessage(
                options.SessionId is null ? "session.started" : "session.reused",
                JsonSerializer.SerializeToElement(new
                {
                    type = options.SessionId is null ? "session.started" : "session.reused",
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

        private string BuildResponse(string prompt, HermesOptions options, string sessionId)
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

            if (prompt.Contains("AI workflows", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: portable automation and repeatable scripts.\n- Trade-off: extra setup, terminal friction, and environment drift.";
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

            var value = prompt[(markerIndex + marker.Length)..].Trim();
            var periodIndex = value.IndexOf('.');
            return periodIndex >= 0 ? value[..periodIndex].Trim() : value;
        }

        private static IReadOnlyList<string> SplitResponse(string response)
        {
            if (response.Length <= 24)
            {
                return [response];
            }

            var midpoint = response.Length / 2;
            return [response[..midpoint], response[midpoint..]];
        }
    }
}
