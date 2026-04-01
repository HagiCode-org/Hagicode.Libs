using HagiCode.Libs.DeepAgents.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console;

public sealed class DeepAgentsConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<DeepAgentsOptions>>
{
    public DeepAgentsConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<DeepAgentsOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = DeepAgentsConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = DeepAgentsConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>>
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
