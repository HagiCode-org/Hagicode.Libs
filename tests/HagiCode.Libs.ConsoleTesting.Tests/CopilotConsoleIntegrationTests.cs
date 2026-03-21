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

    [Fact]
    public async Task DispatchAsync_runs_copilot_default_suite_with_fake_provider()
    {
        var provider = new FakeCopilotProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CopilotConsoleRunner(CopilotConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], CopilotConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] copilot / Ping");
        rendered.ShouldContain("[PASS] copilot / Simple Prompt");
        rendered.ShouldContain("[PASS] copilot / Complex Prompt");
        rendered.ShouldContain("Summary: 3/3 passed");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeCopilotProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CopilotConsoleRunner(CopilotConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], CopilotConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--config-dir <path>");
        rendered.ShouldContain("--auth-source <mode>");
    }

    [Fact]
    public async Task DispatchAsync_normalizes_copilot_aliases_for_provider_commands()
    {
        var provider = new FakeCopilotProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CopilotConsoleRunner(CopilotConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "github-copilot"], CopilotConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("[PASS] copilot / Ping");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var provider = new FakeCopilotProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CopilotConsoleRunner(CopilotConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "codex"], CopilotConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [Fact]
    public async Task DispatchAsync_reports_configuration_failures_for_invalid_copilot_options()
    {
        var provider = new FakeCopilotProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CopilotConsoleRunner(CopilotConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--auth-source", "unsupported"],
            CopilotConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("Unsupported auth source");
    }

    [Fact]
    public async Task DispatchAsync_passes_copilot_specific_runtime_options_to_scenarios()
    {
        var provider = new FakeCopilotProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CopilotConsoleRunner(CopilotConsoleDefinition.Instance, provider, formatter);
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"copilot-console-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);

        try
        {
            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
                [
                    "--test-provider-full",
                    "--model", "claude-sonnet-4.5",
                    "--auth-source", "token",
                    "--github-token", "ghu_test",
                    "--config-dir", ".copilot",
                    "--repo", repositoryPath
                ],
                CopilotConsoleDefinition.Instance,
                runner,
                output);

            exitCode.ShouldBe(0);
            provider.ReceivedOptions.Count.ShouldBe(3);
            provider.ReceivedOptions[0].Model.ShouldBe("claude-sonnet-4.5");
            provider.ReceivedOptions[0].AuthSource.ShouldBe(CopilotAuthSource.GitHubToken);
            provider.ReceivedOptions[0].GitHubToken.ShouldBe("ghu_test");
            provider.ReceivedOptions[0].AdditionalArgs.ShouldContain("--config-dir");
            provider.ReceivedOptions[2].WorkingDirectory.ShouldBe(repositoryPath);
            provider.ReceivedOptions[2].AdditionalArgs.ShouldContain("--add-dir");
            output.ToString().ShouldContain("[PASS] copilot / Repository Analysis");
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_ping_the_real_copilot_cli_when_opted_in()
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
