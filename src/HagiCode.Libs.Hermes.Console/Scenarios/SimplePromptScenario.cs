using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Hermes;

namespace HagiCode.Libs.Hermes.Console.Scenarios;

public static class SimplePromptScenario
{
    public static ProviderConsoleScenario<ICliProvider<HermesOptions>> Create(HermesConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<HermesOptions>>(
            "Simple Prompt",
            "Send a basic prompt and validate the expected pong response.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<HermesOptions> provider,
        HermesConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var result = await HermesScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            executionOptions.CreateBaseOptions(),
            "Reply with exactly the word 'pong'",
            cancellationToken);

        if (result.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Simple Prompt", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = result.AssistantText;
        var success = combined.Contains("pong", StringComparison.OrdinalIgnoreCase);

        return success
            ? new ProviderConsoleScenarioResult(provider.Name, "Simple Prompt", true, 0)
            : new ProviderConsoleScenarioResult(
                provider.Name,
                "Simple Prompt",
                false,
                0,
                ErrorMessage: $"Expected response to contain 'pong' but got: {combined}");
    }
}
