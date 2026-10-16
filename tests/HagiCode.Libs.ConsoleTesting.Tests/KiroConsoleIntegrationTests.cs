using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Kiro.Console;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Kiro;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class KiroConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    /// <summary>The shared dispatch contract row for the Kiro console (see ProviderConsoleDispatchContractTests).</summary>
    internal static ProviderConsoleDispatchRow CreateDispatchRow() => new KiroDispatchRow();

    private sealed class KiroDispatchRow : ProviderConsoleDispatchRow
    {
        private FakeKiroProvider _provider = new();

        public override ProviderConsoleDefinition Definition => KiroConsoleDefinition.Instance;

        public override IReadOnlyList<string> DefaultSuiteScenarioNames { get; } = ["Ping", "Simple Prompt", "Complex Prompt", "Session Resume"];

        public override IReadOnlyList<string> ExpectedHelpText { get; } =
        [
            "--model <model>",
            "--auth-token <token>",
            "--arg <value>"
        ];

        public override string MissingValueOption => "--model";

        public override IProviderConsoleRunner CreateRunner(TextWriter output)
        {
            _provider = new FakeKiroProvider();
            return new KiroConsoleRunner(Definition, _provider, new ProviderConsoleOutputFormatter(output));
        }

        public override IReadOnlyList<string> RuntimeOptionArguments(string repositoryPath) => ["--test-provider-full", "--model", "kiro-default", "--executable", "/custom/kiro-cli-dev", "--auth-method", "token", "--auth-token", "secret", "--bootstrap-method", "authenticate", "--arg", "--profile", "--arg", "smoke", "--repo", repositoryPath];

        public override void AssertRuntimeOptions(string repositoryPath, string renderedOutput)
        {
            _provider.ReceivedOptions.Count.ShouldBe(5);
            _provider.ReceivedOptions[0].Model.ShouldBe("kiro-default");
            _provider.ReceivedOptions[0].ExecutablePath.ShouldBe("/custom/kiro-cli-dev");
            _provider.ReceivedOptions[0].AuthenticationMethod.ShouldBe("token");
            _provider.ReceivedOptions[0].AuthenticationToken.ShouldBe("secret");
            _provider.ReceivedOptions[0].BootstrapMethod.ShouldBe("authenticate");
            _provider.ReceivedOptions[0].ExtraArguments.ShouldBe(["--profile", "smoke"]);
            _provider.ReceivedOptions[3].SessionId.ShouldNotBeNullOrWhiteSpace();
            _provider.ReceivedOptions[4].WorkingDirectory.ShouldBe(repositoryPath);
            renderedOutput.ShouldContain("[PASS] kiro-cli / Repository Summary");
        }

        public override IReadOnlyList<string> ExplicitOptionArguments { get; } = ["--test-provider-full", "--model", "kiro-default", "--auth-method", "token", "--auth-token", "secret"];

        public override void AssertExplicitOptions()
        {
            _provider.ReceivedOptions[0].Model.ShouldBe("kiro-default");
            _provider.ReceivedOptions[0].AuthenticationMethod.ShouldBe("token");
            _provider.ReceivedOptions[0].AuthenticationToken.ShouldBe("secret");
        }
    }

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ProgramMain_can_ping_the_real_kiro_cli_when_opted_in()
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

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ProgramMain_can_run_the_full_default_suite_when_opted_in()
    {
        ExternalGate.Require(RealCliTestsEnvironmentVariable);

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

    private sealed class FakeKiroProvider : ICliProvider<KiroOptions>
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];

        public string Name => "kiro-cli";

        public bool IsAvailable => true;

        public List<KiroOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "kiro-test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            KiroOptions options,
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

        private string BuildResponse(string prompt, KiroOptions options, string sessionId)
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
