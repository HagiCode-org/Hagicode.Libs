using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;
using HagiCode.Libs.ClaudeCode.Console.Scenarios;

namespace HagiCode.Libs.ClaudeCode.Console;

public sealed class ClaudeConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<ClaudeCodeOptions>>
{
    public ClaudeConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<ClaudeCodeOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = ClaudeConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = ClaudeConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>>
        {
            SimplePromptScenario.Create(options),
            ComplexPromptScenario.Create(options),
            SessionResumeScenario.Create(options)
        };

        if (!string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            scenarios.Add(RepositorySummaryScenario.Create(options));
        }

        return scenarios;
    }
}
