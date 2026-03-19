using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console.Scenarios;

public static class SimplePromptScenario
{
    public static ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>> Create(ClaudeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>(
            "Simple Prompt",
            "Send a basic prompt and validate the expected pong response.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var options = executionOptions.CreateBaseOptions() with
        {
            MaxTurns = 1,
        };

        var (messages, _) = await ClaudeScenarioMessageReader.ReadAssistantMessagesAsync(
            provider,
            options,
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
