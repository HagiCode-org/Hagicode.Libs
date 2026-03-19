using System.Diagnostics;
using HagiCode.Libs.Providers;

namespace HagiCode.Libs.ConsoleTesting;

public abstract class ProviderConsoleRunnerBase<TProvider> : IProviderConsoleRunner
    where TProvider : class, ICliProvider
{
    protected ProviderConsoleRunnerBase(
        ProviderConsoleDefinition definition,
        TProvider provider,
        ProviderConsoleOutputFormatter formatter)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    protected ProviderConsoleDefinition Definition { get; }

    protected TProvider Provider { get; }

    protected ProviderConsoleOutputFormatter Formatter { get; }

    protected virtual bool IncludePingInFullSuite => true;

    public virtual async Task<CliProviderTestResult?> PingProviderAsync(
        string providerName,
        IReadOnlyList<string> additionalArgs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(additionalArgs);

        if (!string.Equals(providerName, Definition.DefaultProviderName, StringComparison.OrdinalIgnoreCase))
        {
            var unsupported = new CliProviderTestResult
            {
                ProviderName = providerName,
                Success = false,
                ErrorMessage = $"Provider '{providerName}' is not supported by {Definition.ConsoleName}."
            };
            Formatter.WritePingResult(unsupported);
            return unsupported;
        }

        ValidateAdditionalArgs(additionalArgs);

        var result = await Provider.PingAsync(cancellationToken);
        Formatter.WritePingResult(result);
        return result;
    }

    public virtual Task<ProviderConsoleReport> RunDefaultProviderSuiteAsync(
        IReadOnlyList<string> additionalArgs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(additionalArgs);
        return RunProviderFullSuiteAsync(Definition.DefaultProviderName, additionalArgs, cancellationToken);
    }

    public virtual async Task<ProviderConsoleReport> RunProviderFullSuiteAsync(
        string providerName,
        IReadOnlyList<string> additionalArgs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(additionalArgs);

        if (!string.Equals(providerName, Definition.DefaultProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return WriteFailureReport(
                providerName,
                "Provider Validation",
                $"Provider '{providerName}' is not supported by {Definition.ConsoleName}.");
        }

        IReadOnlyList<ProviderConsoleScenario<TProvider>> scenarios;
        try
        {
            scenarios = CreateScenarios(additionalArgs);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return WriteFailureReport(providerName, "Configuration", ex.Message);
        }

        var results = new List<ProviderConsoleScenarioResult>(scenarios.Count + (IncludePingInFullSuite ? 1 : 0));
        if (IncludePingInFullSuite)
        {
            results.Add(await RunPingScenarioAsync(cancellationToken));
        }

        foreach (var scenario in scenarios)
        {
            results.Add(await RunScenarioAsync(scenario, cancellationToken));
        }

        var report = new ProviderConsoleReport(results, Definition.DefaultProviderName, "full");
        Formatter.WriteReport(report);
        return report;
    }

    protected virtual void ValidateAdditionalArgs(IReadOnlyList<string> additionalArgs)
    {
    }

    protected abstract IReadOnlyList<ProviderConsoleScenario<TProvider>> CreateScenarios(IReadOnlyList<string> additionalArgs);

    private async Task<ProviderConsoleScenarioResult> RunPingScenarioAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await Provider.PingAsync(cancellationToken);
            stopwatch.Stop();
            return new ProviderConsoleScenarioResult(
                result.ProviderName,
                "Ping",
                result.Success,
                stopwatch.ElapsedMilliseconds,
                ErrorMessage: result.ErrorMessage);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ProviderConsoleScenarioResult(
                Definition.DefaultProviderName,
                "Ping",
                false,
                stopwatch.ElapsedMilliseconds,
                ErrorMessage: ex.Message);
        }
    }

    private async Task<ProviderConsoleScenarioResult> RunScenarioAsync(
        ProviderConsoleScenario<TProvider> scenario,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var result = await scenario.ExecuteAsync(Provider, cancellationToken);
            stopwatch.Stop();

            return result with
            {
                ProviderName = string.IsNullOrWhiteSpace(result.ProviderName)
                    ? Definition.DefaultProviderName
                    : result.ProviderName,
                ScenarioName = string.IsNullOrWhiteSpace(result.ScenarioName)
                    ? scenario.Name
                    : result.ScenarioName,
                ElapsedMs = result.ElapsedMs > 0 ? result.ElapsedMs : stopwatch.ElapsedMilliseconds,
                Required = scenario.Required,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new ProviderConsoleScenarioResult(
                Definition.DefaultProviderName,
                scenario.Name,
                false,
                stopwatch.ElapsedMilliseconds,
                scenario.Required,
                "Operation cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new ProviderConsoleScenarioResult(
                Definition.DefaultProviderName,
                scenario.Name,
                false,
                stopwatch.ElapsedMilliseconds,
                scenario.Required,
                ex.Message);
        }
    }

    private ProviderConsoleReport WriteFailureReport(string providerName, string scenarioName, string message)
    {
        var report = new ProviderConsoleReport(
        [
            new ProviderConsoleScenarioResult(providerName, scenarioName, false, 0, ErrorMessage: message)
        ],
        providerName,
        "full");
        Formatter.WriteReport(report);
        return report;
    }
}
