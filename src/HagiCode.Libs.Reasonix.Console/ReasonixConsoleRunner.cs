using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Reasonix;
using HagiCode.Libs.Reasonix.Console.Scenarios;

namespace HagiCode.Libs.Reasonix.Console;

public sealed class ReasonixConsoleRunner : ProviderConsoleRunnerBase<ICliProvider<ReasonixOptions>>
{
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromMilliseconds(250);

    public ReasonixConsoleRunner(
        ProviderConsoleDefinition definition,
        ICliProvider<ReasonixOptions> provider,
        ProviderConsoleOutputFormatter formatter)
        : base(definition, provider, formatter)
    {
    }

    protected override void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
        _ = ReasonixConsoleExecutionOptions.Parse(additionalArgs);
    }

    protected override IReadOnlyList<ProviderConsoleScenario<ICliProvider<ReasonixOptions>>> CreateScenarios(
        IReadOnlyList<string> additionalArgs)
    {
        var options = ReasonixConsoleExecutionOptions.Parse(additionalArgs);
        var scenarios = new List<ProviderConsoleScenario<ICliProvider<ReasonixOptions>>>
        {
            WithTransientRetry(SimplePromptScenario.Create(options)),
            WithTransientRetry(ComplexPromptScenario.Create(options)),
            WithTransientRetry(SessionResumeScenario.Create(options))
        };

        if (!string.IsNullOrWhiteSpace(options.RepositoryPath))
        {
            scenarios.Add(WithTransientRetry(RepositorySummaryScenario.Create(options)));
        }

        return scenarios;
    }

    private static ProviderConsoleScenario<ICliProvider<ReasonixOptions>> WithTransientRetry(
        ProviderConsoleScenario<ICliProvider<ReasonixOptions>> scenario)
    {
        return scenario with
        {
            ExecuteAsync = async (provider, cancellationToken) =>
            {
                try
                {
                    return await scenario.ExecuteAsync(provider, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsTransientOperationCanceled(ex, cancellationToken))
                {
                    await Task.Delay(TransientRetryDelay, cancellationToken).ConfigureAwait(false);
                    return await scenario.ExecuteAsync(provider, cancellationToken).ConfigureAwait(false);
                }
            }
        };
    }

    private static bool IsTransientOperationCanceled(Exception exception, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return exception is OperationCanceledException ||
               exception.Message.Contains("Operation canceled", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("Operation cancelled", StringComparison.OrdinalIgnoreCase);
    }
}
