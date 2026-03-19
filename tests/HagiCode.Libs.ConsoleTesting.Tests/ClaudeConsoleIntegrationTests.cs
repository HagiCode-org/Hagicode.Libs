using System.Runtime.CompilerServices;
using System.Text.Json;
using HagiCode.Libs.ClaudeCode.Console;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Core.Transport;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class ClaudeConsoleIntegrationTests
{
    private const string RealCliTestsEnvironmentVariable = "HAGICODE_REAL_CLI_TESTS";

    [Fact]
    public async Task DispatchAsync_runs_claude_default_suite_with_fake_provider()
    {
        var provider = new FakeClaudeProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new ClaudeConsoleRunner(ClaudeConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], ClaudeConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] claude-code / Ping");
        rendered.ShouldContain("[PASS] claude-code / Simple Prompt");
        rendered.ShouldContain("[PASS] claude-code / Complex Prompt");
        rendered.ShouldContain("[PASS] claude-code / Session Restore");
        rendered.ShouldContain("Summary: 4/4 passed");
    }

    [Fact]
    public async Task DispatchAsync_shows_provider_specific_help_text()
    {
        var provider = new FakeClaudeProvider();
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new ClaudeConsoleRunner(ClaudeConsoleDefinition.Instance, provider, formatter);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(["--help"], ClaudeConsoleDefinition.Instance, runner, output);

        exitCode.ShouldBe(0);
        var rendered = output.ToString();
        rendered.ShouldContain("--test-provider");
        rendered.ShouldContain("--test-provider-full");
        rendered.ShouldContain("--test-all");
        rendered.ShouldContain("--repo <path>");
    }

    [Fact]
    [Trait("Category", "RealCli")]
    public async Task ProgramMain_can_ping_the_real_claude_cli_when_opted_in()
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

    private sealed class FakeClaudeProvider : ICliProvider<ClaudeCodeOptions>
    {
        private string? _lastSecret;

        public string Name => "claude-code";

        public bool IsAvailable => true;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = true,
                Version = "test-1.0.0"
            });
        }

        public async IAsyncEnumerable<CliMessage> ExecuteAsync(
            ClaudeCodeOptions options,
            string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = BuildResponse(prompt, options.WorkingDirectory);
            if (prompt.Contains("Remember the secret word:", StringComparison.OrdinalIgnoreCase))
            {
                _lastSecret = ExtractSecret(prompt);
                response = "ACK";
            }
            else if (options.ContinueConversation &&
                     prompt.Contains("What was the secret word I told you earlier", StringComparison.OrdinalIgnoreCase))
            {
                response = _lastSecret ?? "UNKNOWN";
            }

            yield return new CliMessage(
                "assistant",
                JsonSerializer.SerializeToElement(new
                {
                    message = new
                    {
                        content = new[]
                        {
                            new
                            {
                                type = "text",
                                text = response
                            }
                        }
                    }
                }));
            yield return new CliMessage(
                "result",
                JsonSerializer.SerializeToElement(new { type = "result", subtype = "success" }));
            await Task.Yield();
        }

        private static string BuildResponse(string prompt, string? workingDirectory)
        {
            if (prompt.Contains("exactly the word 'pong'", StringComparison.OrdinalIgnoreCase))
            {
                return "pong";
            }

            if (prompt.Contains("microservices architecture", StringComparison.OrdinalIgnoreCase))
            {
                return "- Advantage: scaling and team ownership.\n- Trade-off: operational overhead and distributed tracing complexity.";
            }

            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                var repositoryName = new DirectoryInfo(workingDirectory).Name;
                return $"Repository {repositoryName} contains src, tests, and app directories with .cs and .json files.";
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
