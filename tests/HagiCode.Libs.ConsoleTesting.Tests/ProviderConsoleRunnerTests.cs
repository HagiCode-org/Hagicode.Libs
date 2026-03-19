using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using Shouldly;

namespace HagiCode.Libs.ConsoleTesting.Tests;

public sealed class ProviderConsoleRunnerTests
{
    private static readonly ProviderConsoleDefinition Definition = new(
        consoleName: "Test.Console",
        providerDisplayName: "Claude Code",
        defaultProviderName: "claude-code",
        helpDescription: "Shared harness test console.");

    [Fact]
    public async Task RunProviderFullSuiteAsync_formats_scenario_results_and_summary()
    {
        var provider = new StubProvider(successfulPing: true);
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new StubRunner(Definition, provider, formatter, failRequiredScenario: false);

        var report = await runner.RunProviderFullSuiteAsync("claude-code", []);

        report.TotalCount.ShouldBe(3);
        report.PassedCount.ShouldBe(3);
        report.IsSuccess.ShouldBeTrue();
        var rendered = output.ToString();
        rendered.ShouldContain("[PASS] claude-code / Ping");
        rendered.ShouldContain("[PASS] claude-code / First Scenario");
        rendered.ShouldContain("Summary: 3/3 passed");
    }

    [Fact]
    public async Task DispatchAsync_returns_non_zero_when_required_scenario_fails()
    {
        var provider = new StubProvider(successfulPing: true);
        using var output = new StringWriter();
        var formatter = new ProviderConsoleOutputFormatter(output);
        var runner = new StubRunner(Definition, provider, formatter, failRequiredScenario: true);

        var exitCode = await ProviderConsoleCommandDispatcher.DispatchAsync(
            ["--test-provider-full"],
            Definition,
            runner,
            output);

        exitCode.ShouldBe(1);
        output.ToString().ShouldContain("required failures: 1");
    }

    private sealed class StubRunner : ProviderConsoleRunnerBase<StubProvider>
    {
        private readonly bool _failRequiredScenario;

        public StubRunner(
            ProviderConsoleDefinition definition,
            StubProvider provider,
            ProviderConsoleOutputFormatter formatter,
            bool failRequiredScenario)
            : base(definition, provider, formatter)
        {
            _failRequiredScenario = failRequiredScenario;
        }

        protected override IReadOnlyList<ProviderConsoleScenario<StubProvider>> CreateScenarios(IReadOnlyList<string> additionalArgs)
        {
            return
            [
                new ProviderConsoleScenario<StubProvider>(
                    "First Scenario",
                    "Always succeeds.",
                    static (provider, _) => Task.FromResult(new ProviderConsoleScenarioResult(provider.Name, "First Scenario", true, 12))),
                new ProviderConsoleScenario<StubProvider>(
                    "Second Scenario",
                    "Optionally fails.",
                    (_, _) => Task.FromResult(new ProviderConsoleScenarioResult(
                        "claude-code",
                        "Second Scenario",
                        !_failRequiredScenario,
                        18,
                        ErrorMessage: _failRequiredScenario ? "required scenario failed" : null)))
            ];
        }
    }

    private sealed class StubProvider(bool successfulPing) : ICliProvider
    {
        public string Name => "claude-code";

        public bool IsAvailable => successfulPing;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task<CliProviderTestResult> PingAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CliProviderTestResult
            {
                ProviderName = Name,
                Success = successfulPing,
                ErrorMessage = successfulPing ? null : "ping failed"
            });
        }
    }
}
