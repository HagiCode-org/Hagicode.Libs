using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.IFlow;

namespace HagiCode.Libs.IFlow.Console.Scenarios;

public static class ComplexPromptScenario
{
    private const int MinResponseLength = 40;

    public static ProviderConsoleScenario<ICliProvider<IFlowOptions>> Create(IFlowConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<IFlowOptions>>(
            "Complex Prompt",
            "Validate a bounded multi-step analysis prompt.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<IFlowOptions> provider,
        IFlowConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var prompt = "Give two short bullet points about terminal-based AI workflows: " +
                     "one advantage and one trade-off. Mention both labels explicitly.";

        var result = await IFlowScenarioMessageReader.ReadExecutionResultAsync(
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
