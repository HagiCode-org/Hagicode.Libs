using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Copilot.Console.Scenarios;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Copilot;

namespace HagiCode.Libs.Copilot.Console;

public sealed class CopilotConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<CopilotOptions>>
{
    public CopilotConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<CopilotOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = CopilotConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<CopilotOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = CopilotConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<CopilotOptions>>>
        {
            SimplePromptScenario.Create(options),
            ComplexPromptScenario.Create(options)
        };

        if (!string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            scenarios.Add(RepositoryAnalysisScenario.Create(options));
        }

        return scenarios;
    }
}
