using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Codex.Console.Scenarios;
using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Codex.Console;

public sealed class CodexConsoleRunner : ProviderConsoleRunnerBase<ICodexProvider>
{
    public CodexConsoleRunner(
        ProviderConsoleDefinition definition,
        ICodexProvider provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = CodexConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICodexProvider>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = CodexConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICodexProvider>>
        {
            SimplePromptScenario.Create(options),
            ComplexPromptScenario.Create(options),
            SessionResumeScenario.Create(options)
        };

        if (!string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            scenarios.Add(RepositoryAnalysisScenario.Create(options));
        }

        return scenarios;
    }
}
