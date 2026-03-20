using HagiCode.Libs.QoderCli.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.QoderCli;

namespace HagiCode.Libs.QoderCli.Console;

public sealed class QoderCliConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<QoderCliOptions>>
{
    public QoderCliConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<QoderCliOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = QoderCliConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<QoderCliOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = QoderCliConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<QoderCliOptions>>>
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
