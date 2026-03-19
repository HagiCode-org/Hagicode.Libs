using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class ProviderConsoleCommandDispatcherTests
{
    private static readonly ProviderConsoleDefinition Definition = new(
        consoleName: "Test.Console",
        providerDisplayName: "Claude Code",
        defaultProviderName: "claude-code",
        helpDescription: "Shared harness test console.",
        aliases: ["claude", "claudecode"]);

    [Fact]
    public async Task DispatchAsync_runs_default_suite_when_no_arguments_are_supplied()
    {
        var runner = new RecordingRunner();
        using var output = new StringWriter();

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync([], Definition, runner, output);

        exitCode.ShouldBe(0);
        runner.DefaultSuiteCalls.ShouldBe(1);
        runner.FullSuiteCalls.ShouldBe(0);
        runner.PingCalls.ShouldBe(0);
    }

    [Fact]
    public async Task DispatchAsync_normalizes_supported_aliases_for_provider_commands()
    {
        var runner = new RecordingRunner();
        using var output = new StringWriter();

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider", "Claude Code"],
            Definition,
            runner,
            output);

        exitCode.ShouldBe(0);
        runner.LastPingProvider.ShouldBe("claude-code");
    }

    [Fact]
    public async Task DispatchAsync_rejects_foreign_provider_names()
    {
        var runner = new RecordingRunner();
        using var output = new StringWriter();

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full", "openai"],
            Definition,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("should use its own dedicated provider console");
        runner.FullSuiteCalls.ShouldBe(0);
    }

    private sealed class RecordingRunner : IProviderConsoleRunner
    {
        public int DefaultSuiteCalls { get; private set; }

        public int FullSuiteCalls { get; private set; }

        public int PingCalls { get; private set; }

        public string? LastPingProvider { get; private set; }

        public Task<CliProviderTestResult?> PingProviderAsync(
            string providerName,
            IReadOnlyList<string> additionalArgs,
            CancellationToken cancellationToken = default)
        {
            PingCalls++;
            LastPingProvider = providerName;
            return Task.FromResult<CliProviderTestResult?>(new CliProviderTestResult
            {
                ProviderName = providerName,
                Success = true,
            });
        }

        public Task<ProviderConsoleReport> RunProviderFullSuiteAsync(
            string providerName,
            IReadOnlyList<string> additionalArgs,
            CancellationToken cancellationToken = default)
        {
            FullSuiteCalls++;
            return Task.FromResult(new ProviderConsoleReport([], providerName, "full"));
        }

        public Task<ProviderConsoleReport> RunDefaultProviderSuiteAsync(
            IReadOnlyList<string> additionalArgs,
            CancellationToken cancellationToken = default)
        {
            DefaultSuiteCalls++;
            return Task.FromResult(new ProviderConsoleReport([], "claude-code", "default"));
        }
    }
}
