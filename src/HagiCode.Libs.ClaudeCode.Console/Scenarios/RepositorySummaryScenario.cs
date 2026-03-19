using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console.Scenarios;

public static class RepositorySummaryScenario
{
    public static ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>> Create(ClaudeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        var repositoryPath = executionOptions.RepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            throw new ArgumentException("--repo requires a value.");
        }

        return new ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>(
            "Repository Analysis",
            $"Analyze repository at {repositoryPath}",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeConsoleExecutionOptions executionOptions,
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
            AddDirectories = [repositoryPath],
            MaxTurns = 2,
        };

        var prompt = "Provide a brief architectural summary of this repository. " +
                     "Mention key project names, technologies used, and the directory structure.";

        var (messages, _) = await ClaudeScenarioMessageReader.ReadAssistantMessagesAsync(
            provider,
            options,
            prompt,
            cancellationToken);

        if (messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Repository Analysis", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = string.Join(" ", messages);
        if (!ContainsRepositoryReference(combined, repositoryPath))
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
            ".cs", ".ts", ".js", ".py", ".json", ".xml", ".yaml", ".yml",
            "src", "test", "bin", "obj", "lib", "app", "api",
            repositoryName
        };

        var normalizedResponse = response.ToLowerInvariant();
        return indicators.Any(indicator => normalizedResponse.Contains(indicator.ToLowerInvariant(), StringComparison.Ordinal));
    }
}
