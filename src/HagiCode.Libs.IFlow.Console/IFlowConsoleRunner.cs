using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.IFlow.Console.Scenarios;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.IFlow;

namespace HagiCode.Libs.IFlow.Console;

public sealed class IFlowConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<IFlowOptions>>
{
    public IFlowConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<IFlowOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = IFlowConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<IFlowOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = IFlowConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<IFlowOptions>>>
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
