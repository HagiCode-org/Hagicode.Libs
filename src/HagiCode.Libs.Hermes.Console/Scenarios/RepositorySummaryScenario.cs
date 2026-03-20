using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Hermes;

namespace HagiCode.Libs.Hermes.Console.Scenarios;

public static class RepositorySummaryScenario
{
    public static ProviderConsoleScenario<ICliProvider<HermesOptions>> Create(HermesConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        var repositoryPath = executionOptions.RepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("--repo requires a value.");
        }

        return new ProviderConsoleScenario<ICliProvider<HermesOptions>>(
            "Repository Summary",
            $"Summarize repository at {repositoryPath}",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<HermesOptions> provider,
        HermesConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var repositoryPath = executionOptions.RepositoryPath!;
        if (!Directory.Exists(repositoryPath))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Repository Summary",
                false,
                0,
                ErrorMessage: $"Repository path does not exist: {repositoryPath}");
        }

        var options = executionOptions.CreateBaseOptions() with
        {
            WorkingDirectory = repositoryPath,
        };

        var prompt = "Provide a brief repository summary. Mention key project names, notable directories, and the technologies in use.";
        var result = await HermesScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            options,
            prompt,
            cancellationToken);

        if (result.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Repository Summary", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = result.AssistantText;
        if (!ContainsRepositoryReference(combined, repositoryPath))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Repository Summary",
                false,
                0,
                ErrorMessage: $"Response does not contain concrete repository references. Response: {combined}");
        }

        return new ProviderConsoleScenarioResult(provider.Name, "Repository Summary", true, 0);
    }

    private static bool ContainsRepositoryReference(string response, string repositoryPath)
    {
        var repositoryName = new DirectoryInfo(repositoryPath).Name;
        var indicators =
            new[]
            {
                ".cs", ".ts", ".js", ".json", ".yaml", ".yml",
                "src", "tests", "docs", "app", "packages",
                repositoryName
            };

        var normalizedResponse = response.ToLowerInvariant();
        return indicators.Any(indicator => normalizedResponse.Contains(indicator.ToLowerInvariant(), StringComparison.Ordinal));
    }
}
