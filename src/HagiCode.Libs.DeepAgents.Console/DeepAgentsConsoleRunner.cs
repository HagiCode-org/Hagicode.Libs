using HagiCode.Libs.DeepAgents.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console;

public sealed class DeepAgentsConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<DeepAgentsOptions>>
{
    public DeepAgentsConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<DeepAgentsOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = DeepAgentsConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = DeepAgentsConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>>
        {
            SimplePromptScenario.Create(options),
            ComplexPromptScenario.Create(options),
            SessionResumeScenario.Create(options)
        };

        if (options.UsesBypassMode)
        {
            scenarios.Add(BypassBashPingScenario.Create(options));
        }

        if (!string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            scenarios.Add(RepositorySummaryScenario.Create(options));
        }

        scenarios.AddRange(CreateToolcallScenarios(options));

        return scenarios;
    }

    private static IReadOnlyList<ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>> CreateToolcallScenarios(
        DeepAgentsConsoleExecutionOptions options)
    {
        if (!options.ToolcallEnabled && !options.HasToolcallSelection)
        {
            return [];
        }

        var allScenarios = new Dictionary<string, ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>>(StringComparer.Ordinal)
        {
            [DeepAgentsToolcallCaseCatalog.Parsing] = ToolcallParsingScenario.Create(options),
            [DeepAgentsToolcallCaseCatalog.Failure] = ToolcallFailureScenario.Create(options),
            [DeepAgentsToolcallCaseCatalog.Mixed] = ToolcallMixedTranscriptScenario.Create(options)
        };

        var selectedCase = DeepAgentsToolcallCaseCatalog.Resolve(options.ToolcallCaseName);
        if (selectedCase is null)
        {
            if (!string.IsNullOrWhiteSpace(options.ToolcallCaseName))
            {
                throw new InvalidOperationException(
                    $"Unknown DeepAgents toolcall case '{options.ToolcallCaseName}'. Available cases: {DeepAgentsToolcallCaseCatalog.FormatAvailableCases()}.");
            }

            return allScenarios.Values.ToArray();
        }

        return [allScenarios[selectedCase]];
    }
}
