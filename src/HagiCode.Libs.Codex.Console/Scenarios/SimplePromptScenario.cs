using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Codex;

namespace HagiCode.Libs.Codex.Console.Scenarios;

public static class SimplePromptScenario
{
    public static ProviderConsoleScenario<ICliProvider<CodexOptions>> Create(CodexConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<CodexOptions>>(
            "Simple Prompt",
            "Send a basic prompt and validate the expected pong response.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<CodexOptions> provider,
        CodexConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var messages = await CodexScenarioMessageReader.ReadAssistantMessagesAsync(
            provider,
            executionOptions.CreateBaseOptions(),
            "Reply with exactly the word 'pong'",
            cancellationToken);

        if (messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Simple Prompt", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = string.Join(" ", messages);
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
