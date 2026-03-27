using HagiCode.Libs.Gemini.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Gemini;

namespace HagiCode.Libs.Gemini.Console;

public sealed class GeminiConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<GeminiOptions>>
{
    public GeminiConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<GeminiOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = GeminiConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<GeminiOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = GeminiConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<GeminiOptions>>>
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
