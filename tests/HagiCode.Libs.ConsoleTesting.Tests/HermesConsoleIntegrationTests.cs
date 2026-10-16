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

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ProgramMain_can_ping_the_real_hermes_cli_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

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

    /// <summary>The shared dispatch contract row for the Hermes console (see ProviderConsoleDispatchContractTests).</summary>
    internal static ProviderConsoleDispatchRow CreateDispatchRow() => new HermesDispatchRow();

    private sealed class HermesDispatchRow : ProviderConsoleDispatchRow
    {
        private FakeHermesProvider _provider = new();

        public override ProviderConsoleDefinition Definition => HermesConsoleDefinition.Instance;

        public override IReadOnlyList<string> DefaultSuiteScenarioNames { get; } = ["Ping", "Simple Prompt", "Complex Prompt", "Memory Reuse"];

        public override IReadOnlyList<string> ExpectedHelpText { get; } =
        [
            "--model <model>",
            "--arguments <value>"
        ];

        public override IReadOnlyList<string> InvalidOptionArguments => ["--test-provider-full", "--arguments", "\"unterminated"];

        public override string InvalidOptionMessage => "unterminated quoted value";

        public override IProviderConsoleRunner CreateRunner(TextWriter output)
        {
            _provider = new FakeHermesProvider();
            return new HermesConsoleRunner(Definition, _provider, new ProviderConsoleOutputFormatter(output));
        }

        public override IReadOnlyList<string> RuntimeOptionArguments(string repositoryPath)
        {
            return ["--test-provider-full", "--model", "hermes/default", "--executable", "/custom/hermes", "--arguments", "acp --profile smoke", "--repo", repositoryPath];
        }

        public override void AssertRuntimeOptions(string repositoryPath, string renderedOutput)
        {
            _provider.ReceivedOptions.Count.ShouldBe(5);
            _provider.ReceivedOptions[0].Model.ShouldBe("hermes/default");
            _provider.ReceivedOptions[0].ExecutablePath.ShouldBe("/custom/hermes");
            _provider.ReceivedOptions[0].Arguments.ShouldBe(["acp", "--profile", "smoke"]);
            _provider.ReceivedOptions[3].SessionId.ShouldNotBeNullOrWhiteSpace();
            _provider.ReceivedOptions[4].WorkingDirectory.ShouldBe(repositoryPath);
            renderedOutput.ShouldContain("[PASS] hermes / Repository Summary");
        }

        public override IReadOnlyList<string> ExplicitOptionArguments { get; } = ["--test-provider-full", "--model", "hermes/explicit"];

        public override void AssertExplicitOptions()
        {
            _provider.ReceivedOptions[0].Model.ShouldBe("hermes/explicit");
        }
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
