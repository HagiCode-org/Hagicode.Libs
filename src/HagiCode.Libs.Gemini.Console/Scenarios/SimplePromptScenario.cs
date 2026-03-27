using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Gemini;

namespace HagiCode.Libs.Gemini.Console.Scenarios;

public static class SimplePromptScenario
{
    public static ProviderConsoleScenario<ICliProvider<GeminiOptions>> Create(GeminiConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<GeminiOptions>>(
            "Simple Prompt",
            "Send a basic prompt and validate the expected pong response.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<GeminiOptions> provider,
        GeminiConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var result = await GeminiScenarioMessageReader.ReadExecutionResultAsync(
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
