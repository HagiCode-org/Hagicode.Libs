using HagiCode.Libs.OpenCode.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.OpenCode;

namespace HagiCode.Libs.OpenCode.Console;

public sealed class OpenCodeConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<OpenCodeOptions>>
{
    public OpenCodeConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<OpenCodeOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = OpenCodeConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<OpenCodeOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = OpenCodeConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<OpenCodeOptions>>>
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
