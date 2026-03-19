using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.ClaudeCode;

namespace HagiCode.Libs.ClaudeCode.Console.Scenarios;

public static class ComplexPromptScenario
{
    private const int MinResponseLength = 40;
    private const int MaxTurns = 2;

    public static ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>> Create(ClaudeConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<ClaudeCodeOptions>>(
            "Complex Prompt",
            "Validate a multi-step analysis prompt with bounded turns.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<ClaudeCodeOptions> provider,
        ClaudeConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var options = executionOptions.CreateBaseOptions() with
        {
            MaxTurns = MaxTurns,
        };

        var prompt = "Give two short bullet points about microservices architecture: " +
                     "one advantage and one trade-off. Mention both labels explicitly.";

        var (messages, assistantMessageCount) = await ClaudeScenarioMessageReader.ReadAssistantMessagesAsync(
            provider,
            options,
            prompt,
            cancellationToken);

        if (messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Complex Prompt", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = string.Join(" ", messages);
        if (combined.Length < MinResponseLength)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Complex Prompt",
                false,
                0,
                ErrorMessage: $"Response too short: {combined.Length} chars (minimum {MinResponseLength}). Response: {combined}");
        }

        if (assistantMessageCount > MaxTurns)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Complex Prompt",
                false,
                0,
                ErrorMessage: $"Provider returned {assistantMessageCount} assistant messages, exceeding MaxTurns={MaxTurns}.");
        }

        var normalized = combined.ToLowerInvariant();
        var mentionsAdvantage = normalized.Contains("advantage", StringComparison.Ordinal) ||
                                normalized.Contains("pro", StringComparison.Ordinal);
        var mentionsTradeOff = normalized.Contains("trade-off", StringComparison.Ordinal) ||
                               normalized.Contains("tradeoff", StringComparison.Ordinal) ||
                               normalized.Contains("con", StringComparison.Ordinal) ||
                               normalized.Contains("disadvantage", StringComparison.Ordinal);
        if (!mentionsAdvantage || !mentionsTradeOff)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Complex Prompt",
                false,
                0,
                ErrorMessage: $"Response must mention both an advantage and a trade-off. Response: {combined}");
        }

        return new ProviderConsoleScenarioResult(provider.Name, "Complex Prompt", true, 0);
    }
}
