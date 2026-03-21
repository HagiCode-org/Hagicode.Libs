using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Copilot;

namespace HagiCode.Libs.Copilot.Console.Scenarios;

public static class RepositoryAnalysisScenario
{
    public static ProviderConsoleScenario<ICliProvider<CopilotOptions>> Create(CopilotConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        var repositoryPath = executionOptions.RepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("--repo requires a value.");
        }

        return new ProviderConsoleScenario<ICliProvider<CopilotOptions>>(
            "Repository Analysis",
            $"Analyze repository at {repositoryPath}",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<CopilotOptions> provider,
        CopilotConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var repositoryPath = executionOptions.RepositoryPath!;
        if (!Directory.Exists(repositoryPath))
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Repository Analysis",
                false,
                0,
                ErrorMessage: $"Repository path does not exist: {repositoryPath}");
        }

        var options = executionOptions.CreateBaseOptions() with
        {
            WorkingDirectory = repositoryPath,
            AdditionalArgs = [.. executionOptions.AdditionalArgs, "--add-dir", repositoryPath]
        };

        var prompt = "Provide a brief architectural summary of this repository. " +
                     "Mention key technologies, notable directories, and any important project names.";

        var result = await CopilotScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            options,
            prompt,
            cancellationToken);

        if (result.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Repository Analysis", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = string.Join(" ", result.Messages);
        if (!ContainsRepositoryReference(combined, repositoryPath) && result.ToolEvents.Count == 0)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Repository Analysis",
                false,
                0,
                ErrorMessage: $"Response does not contain concrete repository references. Response: {combined}");
        }

        return new ProviderConsoleScenarioResult(provider.Name, "Repository Analysis", true, 0);
    }

    private static bool ContainsRepositoryReference(string response, string repositoryPath)
    {
        var repositoryName = new DirectoryInfo(repositoryPath).Name;
        var indicators = new[]
        {
            ".cs", ".ts", ".js", ".json", ".yaml", ".yml",
            "src", "test", "tests", "docs", "app", "api",
            repositoryName
        };

        var normalizedResponse = response.ToLowerInvariant();
        return indicators.Any(indicator => normalizedResponse.Contains(indicator.ToLowerInvariant(), StringComparison.Ordinal));
    }
}
