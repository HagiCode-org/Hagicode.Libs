using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Dsh;

namespace HagiCode.Libs.Dsh.Console.Scenarios;

public static class ComplexPromptScenario
{
    private const int MinResponseLength = 40;

    public static ProviderConsoleScenario<ICliProvider<DshOptions>> Create(DshConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DshOptions>>(
            "Complex Prompt",
            "Validate a bounded multi-step analysis prompt.",
            async (provider, cancellationToken) => executionOptions.WithVerboseDetails(
                await ExecuteAsync(provider, executionOptions, cancellationToken)));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DshOptions> provider,
        DshConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var prompt = "Give two short bullet points about software testing: " +
                     "one advantage and one trade-off. Mention both labels explicitly.";

        var result = await DshScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            executionOptions.CreateBaseOptions(),
            prompt,
            cancellationToken);

        if (result.Messages.Count == 0)
        {
            return new ProviderConsoleScenarioResult(provider.Name, "Complex Prompt", false, 0, ErrorMessage: "No assistant messages received from provider.");
        }

        var combined = result.AssistantText;
        if (combined.Length < MinResponseLength)
        {
            return new ProviderConsoleScenarioResult(
                provider.Name,
                "Complex Prompt",
                false,
                0,
                ErrorMessage: $"Response too short: {combined.Length} chars (minimum {MinResponseLength}). Response: {combined}");
        }

        var normalized = combined.ToLowerInvariant();
        var mentionsAdvantage = normalized.Contains("advantage", StringComparison.Ordinal);
        var mentionsTradeOff = normalized.Contains("trade-off", StringComparison.Ordinal) ||
                               normalized.Contains("tradeoff", StringComparison.Ordinal);
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
