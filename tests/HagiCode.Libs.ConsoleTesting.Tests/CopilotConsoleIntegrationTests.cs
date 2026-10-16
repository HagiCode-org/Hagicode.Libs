using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Copilot.Console;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Copilot;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class CopilotConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Test]
    [Category("RealCli")]
    [Category("External")]
    public async Task ProgramMain_can_ping_the_real_copilot_cli_when_opted_in()
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

    /// <summary>The shared dispatch contract row for the Copilot console (see ProviderConsoleDispatchContractTests).</summary>
    internal static ProviderConsoleDispatchRow CreateDispatchRow() => new CopilotDispatchRow();

    private sealed class CopilotDispatchRow : ProviderConsoleDispatchRow
    {
        private FakeCopilotProvider _provider = new();

        public override ProviderConsoleDefinition Definition => CopilotConsoleDefinition.Instance;

        public override IReadOnlyList<string> DefaultSuiteScenarioNames { get; } = ["Ping", "Simple Prompt", "Complex Prompt"];

        public override IReadOnlyList<string> ExpectedHelpText { get; } =
        [
            "--config-dir <path>",
            "--auth-source <mode>"
        ];

        public override IReadOnlyList<string> InvalidOptionArguments => ["--test-provider-full", "--auth-source", "unsupported"];

        public override string InvalidOptionMessage => "Unsupported auth source";

        public override IProviderConsoleRunner CreateRunner(TextWriter output)
        {
            _provider = new FakeCopilotProvider();
            return new CopilotConsoleRunner(Definition, _provider, new ProviderConsoleOutputFormatter(output));
        }

        public override IReadOnlyList<string> RuntimeOptionArguments(string repositoryPath)
        {
            return ["--test-provider-full", "--model", "claude-sonnet-4.5", "--auth-source", "token", "--github-token", "ghu_test", "--config-dir", ".copilot", "--repo", repositoryPath];
        }

        public override void AssertRuntimeOptions(string repositoryPath, string renderedOutput)
        {
            _provider.ReceivedOptions.Count.ShouldBe(3);
            _provider.ReceivedOptions[0].Model.ShouldBe("claude-sonnet-4.5");
            _provider.ReceivedOptions[0].AuthSource.ShouldBe(CopilotAuthSource.GitHubToken);
            _provider.ReceivedOptions[0].GitHubToken.ShouldBe("ghu_test");
            _provider.ReceivedOptions[0].AdditionalArgs.ShouldContain("--config-dir");
            _provider.ReceivedOptions[2].WorkingDirectory.ShouldBe(repositoryPath);
            _provider.ReceivedOptions[2].AdditionalArgs.ShouldContain("--add-dir");
            renderedOutput.ShouldContain("[PASS] copilot / Repository Analysis");
        }

        public override IReadOnlyList<string> ExplicitOptionArguments { get; } = ["--test-provider-full", "--model", "gpt-5"];

        public override void AssertExplicitOptions()
        {
            _provider.ReceivedOptions[0].Model.ShouldBe("gpt-5");
        }
    }

    private sealed class FakeCopilotProvider : ICliProvider<CopilotOptions>
    {
        public string Name => "copilot";

        public bool IsAvailable => true;

        public List<CopilotOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "copilot-test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            CopilotOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var response = BuildResponse(prompt, options.WorkingDirectory);

            if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
            {
                yield return CreateMessage("tool.started", new
                {
                    type = "tool.started",
                    tool_name = "repo-scan",
                    tool_call_id = "tool-1"
                });
                yield return CreateMessage("tool.completed", new
                {
                    type = "tool.completed",
                    tool_name = "repo-scan",
                    tool_call_id = "tool-1",
                    text = "scanned repository",
                    failed = false
                });
            }

            yield return CreateMessage("assistant", new
            {
                type = "assistant",
                text = response
            });
            yield return CreateMessage("result", new
            {
                type = "result",
                status = "completed"
            });
            await Task.Yield();
        }

        private static string BuildResponse(string prompt, string? workingDirectory)
        {
            if (prompt.Contains("exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("software architecture", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: modular scaling and clearer ownership.\n- Trade-off: extra coordination and operational complexity.";
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var repositoryName = new DirectoryInfo(workingDirectory).Name;
                return $"Repository {repositoryName} contains src, tests, docs, and app folders with .cs and .json files.";
            }

            return "pong";
        }

        private static CliMessage CreateMessage(string type, object payload)
            => new(type, JsonSerializer.SerializeToElement(payload));
    }
}
