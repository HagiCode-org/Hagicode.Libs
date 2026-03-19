using HagiCode.Libs.Providers;

namespace HagiCode.Libs.ConsoleTesting;

public interface IProviderConsoleRunner
{
    Task<CliProviderTestResult?> PingProviderAsync(
        string providerName,
        IReadOnlyList<string> additionalArgs,
        CancellationToken cancellationToken = default);

    Task<ProviderConsoleReport> RunProviderFullSuiteAsync(
        string providerName,
        IReadOnlyList<string> additionalArgs,
        CancellationToken cancellationToken = default);

    Task<ProviderConsoleReport> RunDefaultProviderSuiteAsync(
        IReadOnlyList<string> additionalArgs,
        CancellationToken cancellationToken = default);
}
