using HagiCode.Libs.Junie.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Junie;

namespace HagiCode.Libs.Junie.Console;

public sealed class JunieConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<JunieOptions>>
{
    public JunieConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<JunieOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = JunieConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<JunieOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = JunieConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<JunieOptions>>>
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
