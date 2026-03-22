using HagiCode.Libs.Kimi.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Kimi;

namespace HagiCode.Libs.Kimi.Console;

public sealed class KimiConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<KimiOptions>>
{
    public KimiConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<KimiOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = KimiConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<KimiOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = KimiConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<KimiOptions>>>
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
