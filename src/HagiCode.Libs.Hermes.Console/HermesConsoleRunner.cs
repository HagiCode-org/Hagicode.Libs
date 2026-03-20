using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Hermes.Console.Scenarios;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Hermes;

namespace HagiCode.Libs.Hermes.Console;

public sealed class HermesConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<HermesOptions>>
{
    public HermesConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<HermesOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = HermesConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<HermesOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = HermesConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<HermesOptions>>>
        {
            SimplePromptScenario.Create(options),
            ComplexPromptScenario.Create(options),
            MemoryReuseScenario.Create(options)
        };

        if (!string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            scenarios.Add(RepositorySummaryScenario.Create(options));
        }

        return scenarios;
    }
}
