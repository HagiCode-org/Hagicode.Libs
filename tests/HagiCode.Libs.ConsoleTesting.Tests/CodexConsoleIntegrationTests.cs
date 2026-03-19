using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.Codex.Console;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codex;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class CodexConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Fact]
    public async Task DispatchAsync_runs_codex_default_suite_with_fake_provider()
    {
        var provider = new FakeCodexProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CodexConsoleRunner(CodexConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], CodexConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] codex / Ping");
        rendered.ShouldContain("[PASS] codex / Simple Prompt");
        rendered.ShouldContain("[PASS] codex / Complex Prompt");
        rendered.ShouldContain("[PASS] codex / Session Resume");
        rendered.ShouldContain("Summary: 4/4 passed");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeCodexProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CodexConsoleRunner(CodexConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], CodexConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--sandbox <mode>");
        rendered.ShouldContain("--approval-policy <mode>");
    }

    [Fact]
    public async Task DispatchAsync_normalizes_codex_aliases_for_provider_commands()
    {
        var provider = new FakeCodexProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CodexConsoleRunner(CodexConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "codex-cli"], CodexConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        output.ToString().ShouldContain("[PASS] codex / Ping");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var provider = new FakeCodexProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CodexConsoleRunner(CodexConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--test-provider", "claude-code"], CodexConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("dedicated provider console");
    }

    [Fact]
    public async Task DispatchAsync_reports_configuration_failures_for_invalid_codex_options()
    {
        var provider = new FakeCodexProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CodexConsoleRunner(CodexConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "--sandbox", "unsupported"],
            CodexConsoleDefinition.Instance,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("Unsupported sandbox mode");
    }

    [Fact]
    public async Task DispatchAsync_passes_codex_specific_runtime_options_to_scenarios()
    {
        var provider = new FakeCodexProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new CodexConsoleRunner(CodexConsoleDefinition.Instance, provider, formatter);
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"codex-console-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);

        try
        {
            var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
                [
                    "--test-provider-full",
                    "--model", "gpt-5-codex",
                    "--sandbox", "workspace-write",
                    "--approval-policy", "never",
                    "--repo", repositoryPath
                ],
                CodexConsoleDefinition.Instance,
                runner,
                output);

            exitCode.ShouldBe(0);
            provider.ReceivedOptions.Count.ShouldBe(5);
            provider.ReceivedOptions[0].Model.ShouldBe("gpt-5-codex");
            provider.ReceivedOptions[0].SandboxMode.ShouldBe("workspace-write");
            provider.ReceivedOptions[0].ApprovalPolicy.ShouldBe("never");
            provider.ReceivedOptions[3].ThreadId.ShouldNotBeNullOrWhiteSpace();
            provider.ReceivedOptions[4].WorkingDirectory.ShouldBe(repositoryPath);
            provider.ReceivedOptions[4].AddDirectories.ShouldBe([repositoryPath]);
            output.ToString().ShouldContain("[PASS] codex / Repository Analysis");
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_ping_the_real_codex_cli_when_opted_in()
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

    private sealed class FakeCodexProvider : ICliProvider<CodexOptions>
    {
        private readonly Dictionary<string, string> _threadSecrets = [];

        public string Name => "codex";

        public bool IsAvailable => true;

        public List<CodexOptions> ReceivedOptions { get; } = [];

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "codex-test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            CodexOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedOptions.Add(options);
            var threadId = options.ThreadId ?? $"thread-{ReceivedOptions.Count}";
            var response = BuildResponse(prompt, options.WorkingDirectory, threadId);

            if (options.ThreadId is null)
            {
                yield return new CliMessage(
                    "thread.started",
                    JsonSerializer.SerializeToElement(new
                    {
                        type = "thread.started",
                        thread_id = threadId
                    }));
            }

            yield return new CliMessage(
                "item.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "item.completed",
                    item = new
                    {
                        type = "agent_message",
                        text = response
                    }
                }));
            yield return new CliMessage(
                "turn.completed",
                JsonSerializer.SerializeToElement(new
                {
                    type = "turn.completed",
                    usage = new
                    {
                        input_tokens = 1,
                        cached_input_tokens = 0,
                        output_tokens = 1
                    }
                }));
            await Task.Yield();
        }

        private string BuildResponse(string prompt, string? workingDirectory, string threadId)
        {
            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                var secret = ExtractSecret(prompt);
                if (secret is not null)
                {
                    _threadSecrets[threadId] = secret;
                }

                return "ACK";
            }

            if (prompt.Contains("What was the secret word I told you earlier", StringComparison.OrdinalIgnoreCase))
            {
                return _threadSecrets.TryGetValue(threadId, out var secret)
                    ? secret
                    : "UNKNOWN";
            }

            if (prompt.Contains("exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("software architecture", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: modular scaling and clearer ownership.\n- Trade-off: extra coordination, deployment overhead, and operational complexity.";
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var repositoryName = new DirectoryInfo(workingDirectory).Name;
                return $"Repository {repositoryName} contains src, tests, docs, and app folders with .cs and .json files.";
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
    }
}
