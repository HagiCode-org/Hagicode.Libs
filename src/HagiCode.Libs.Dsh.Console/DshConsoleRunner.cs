using HagiCode.Libs.Dsh.Console.Scenarios;
using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Dsh;

namespace HagiCode.Libs.Dsh.Console;

public sealed class DshConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<DshOptions>>
{
    public DshConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<DshOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = DshConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<DshOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = DshConsoleExecutionOptions.Parse(additionalArgs);
        return
        [
            SimplePromptScenario.Create(options),
            ComplexPromptScenario.Create(options),
            SessionResumeScenario.Create(options)
        ];
    }
}
