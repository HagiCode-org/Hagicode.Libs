using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

public static class SimplePromptScenario
{
    public static ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>> Create(DeepAgentsConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>(
            "Simple Prompt",
            "Send a basic prompt and validate the expected pong response.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var result = await DeepAgentsScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            executionOptions.CreateBaseOptions(),
            "Reply with exactly the word 'pong'",
            cancellationToken);

        if (result.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Simple Prompt", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        return result.AssistantText.Contains("pong", StringComparison.OrdinalIgnoreCase)
            ? new ProviderConsoleScenarioResult(provider.Name, "Simple Prompt", true, 0)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Simple Prompt",
                false,
                0,
                ErrorMessage: $"Expected response to contain 'pong' but got: {result.AssistantText}");
    }
}
