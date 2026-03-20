using HagiCode.Libs.Codebuddy.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codebuddy;

namespace HagiCode.Libs.Codebuddy.Console;

public sealed class CodebuddyConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<CodebuddyOptions>>
{
    public CodebuddyConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<CodebuddyOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = CodebuddyConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<CodebuddyOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = CodebuddyConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<CodebuddyOptions>>>
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
