using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.DeepAgents.Console;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class DeepAgentsConsoleIntegrationTests
{
    [Fact]
    public async Task DispatchAsync_runs_deepagents_default_suite_with_fake_provider()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] deepagents / Ping");
        rendered.ShouldContain("[PASS] deepagents / Simple Prompt");
        rendered.ShouldContain("[PASS] deepagents / Complex Prompt");
        rendered.ShouldContain("[PASS] deepagents / Session Resume");
        rendered.ShouldContain("Summary: 4/4 passed");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--model <model>");
        rendered.ShouldContain("--workspace <path>");
        rendered.ShouldContain("--name <name>");
        rendered.ShouldContain("--description <text>");
        rendered.ShouldContain("--skill <path>");
        rendered.ShouldContain("--executable <path>");
        rendered.ShouldContain("--arg <value>");
    }

    [Fact]
    public async Task DispatchAsync_accepts_the_deepagents_acp_alias()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "deepagents-acp"], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("[PASS] deepagents / Ping");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "codex"], DeepAgentsConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [Fact]
    public async Task DispatchAsync_reports_configuration_failures_for_invalid_deepagents_options()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--workspace"],
            DeepAgentsConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("--workspace requires a value");
    }

    [Fact]
    public async Task DispatchAsync_passes_runtime_options_to_scenarios()
    {
        var provider = new FakeDeepAgentsProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new DeepAgentsConsoleRunner(DeepAgentsConsoleDefinition.Instance, provider, formatter);
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"deepagents-console-repo-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(Path.GetTempPath(), $"deepagents-console-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        Directory.CreateDirectory(workspacePath);

        try
        {
            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
                [
                    "--test-provider-full",
                    "--repo", repositoryPath,
                    "--model", "glm-5.1",
                    "--workspace", workspacePath,
                    "--name", "deepagents-bot",
                    "--description", "DeepAgents test bot",
                    "--skill", "./skills",
                    "--memory", "./AGENTS.md",
                    "--executable", "/tmp/deepagents-acp",
                    "--arg", "--debug"
                ],
                DeepAgentsConsoleDefinition.Instance,
                runner,
                output);

            exitCode.ShouldBe(0);
            provider.ReceivedOptions.Count.ShouldBe(5);
            provider.ReceivedOptions[0].Model.ShouldBe("glm-5.1");
            provider.ReceivedOptions[0].WorkspaceRoot.ShouldBe(workspacePath);
            provider.ReceivedOptions[0].AgentName.ShouldBe("deepagents-bot");
            provider.ReceivedOptions[0].AgentDescription.ShouldBe("DeepAgents test bot");
            provider.ReceivedOptions[0].SkillsDirectories.ShouldBe(["./skills"]);
            provider.ReceivedOptions[0].MemoryFiles.ShouldBe(["./AGENTS.md"]);
            provider.ReceivedOptions[0].ExecutablePath.ShouldBe("/tmp/deepagents-acp");
            provider.ReceivedOptions[0].ExtraArguments.ShouldBe(["--debug"]);
            provider.ReceivedOptions[3].SessionId.ShouldNotBeNullOrWhiteSpace();
            provider.ReceivedOptions[4].WorkspaceRoot.ShouldBe(repositoryPath);
            output.ToString().ShouldContain("[PASS] deepagents / Repository Summary");
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private sealed class FakeDeepAgentsProvider : ICliProvider<DeepAgentsOptions>
    {
        private readonly Dictionary<string, string> _sessionSecrets = [];

        public string Name => "deepagents";

        public bool IsAvailable => true;

        public List<DeepAgentsOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "deepagents-test-0.1.7"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            DeepAgentsOptions options,
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

        private string BuildResponse(string prompt, DeepAgentsOptions options, string sessionId)
        {
            if (prompt.Contains("Reply with exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("Give two short bullet points about software testing", StringComparison.OrdinalIgnoreCase))
            {
                return "Advantage: catches regressions early.\nTrade-off: requires ongoing maintenance.";
            }

            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var marker = "Remember the secret word:";
                var startIndex = prompt.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length;
                var endIndex = prompt.IndexOf('.', startIndex);
                var secret = prompt[startIndex..endIndex].Trim();
                _sessionSecrets[sessionId] = secret;
                return "ACK";
            }

            if (prompt.Contains("What was the secret word", StringComparison.OrdinalIgnoreCase))
            {
                return _sessionSecrets.TryGetValue(sessionId, out var secret) ? secret : "UNKNOWN";
            }

            if (prompt.Contains("repository summary", StringComparison.OrdinalIgnoreCase) ||
                prompt.Contains("Provide a brief repository summary", StringComparison.OrdinalIgnoreCase))
            {
                var repoPath = options.WorkspaceRoot ?? options.WorkingDirectory ?? Directory.GetCurrentDirectory();
                var repoName = new DirectoryInfo(repoPath).Name;
                return $"{repoName}: src, tests, docs, .cs files, and JSON configs are present.";
            }

            return $"Workspace={(options.WorkspaceRoot ?? options.WorkingDirectory ?? "(none)")}; Agent={options.AgentName ?? "deepagents"}";
        }

        private static IReadOnlyList<string> SplitResponse(string response)
        {
            if (response.Length <= 32)
            {
                return [response];
            }

            var midpoint = response.Length / 2;
            return [response[..midpoint], response[midpoint..]];
        }
    }
}
