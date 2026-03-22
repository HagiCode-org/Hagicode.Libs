using HagiCode.Libs.Kiro.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Kiro;

namespace HagiCode.Libs.Kiro.Console;

public sealed class KiroConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<KiroOptions>>
{
    public KiroConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<KiroOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = KiroConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<KiroOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = KiroConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<KiroOptions>>>
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
