using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Pi.Console.Scenarios;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Pi;

namespace HagiCode.Libs.Pi.Console;

public sealed class PiConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<PiOptions>>
{
    public PiConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<PiOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = PiConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<PiOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = PiConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<PiOptions>>>
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
