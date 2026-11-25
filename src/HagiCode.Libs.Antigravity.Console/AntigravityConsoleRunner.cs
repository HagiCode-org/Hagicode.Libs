using HagiCode.Libs.Antigravity.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Antigravity;

namespace HagiCode.Libs.Antigravity.Console;

public sealed class AntigravityConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<AntigravityOptions>>
{
    public AntigravityConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<AntigravityOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = AntigravityConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<AntigravityOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = AntigravityConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<AntigravityOptions>>>
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
