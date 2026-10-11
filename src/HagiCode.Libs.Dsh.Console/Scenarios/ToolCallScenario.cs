using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.Dsh;

namespace HagiCode.Libs.Dsh.Console.Scenarios;

public static class ToolCallScenario
{
    private const string ScenarioName = "Tool Call";

    public static ProviderConsoleScenario<ICliProvider<DshOptions>> Create(DshConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DshOptions>>(
            ScenarioName,
            "Validate that a tool call streams a started and a completed update, followed by assistant text.",
            async (provider, cancellationToken) => await ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DshOptions> provider,
        DshConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        const string prompt = "First say one short sentence about what you are about to do. " +
                              "Then use your shell tool to run the read-only command `ls` in the current directory. " +
                              "Finally summarize what you found in one sentence.";

        var result = await DshScenarioMessageReader.ReadExecutionResultAsync(
            provider,
            executionOptions.CreateBaseOptions(),
            prompt,
            cancellationToken);

        return WithToolDetails(
            executionOptions,
            executionOptions.WithVerboseDetails(Evaluate(provider.Name, result)),
            result);
    }

    private static ProviderConsoleScenarioResult Evaluate(string providerName, DshScenarioExecutionResult result)
    {
        if (result.ToolCalls.Count == 0)
        {
            return Fail(
                providerName,
                "No tool call was observed. dsh answered without emitting a tool.call message, " +
                "so tool activity is not reaching the shared provider (or the model skipped the tool).");
        }

        foreach (var toolCall in result.ToolCalls)
        {
            if (!string.Equals(toolCall.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                var problem = string.Equals(toolCall.Status, "failed", StringComparison.OrdinalIgnoreCase)
                    ? "failed"
                    : "never completed";
                return Fail(
                    providerName,
                    $"Tool call '{toolCall.Id}' ({toolCall.Name}) {problem}; final status: {toolCall.Status}.");
            }
        }

        if (result.Messages.Count == 0 || string.IsNullOrWhiteSpace(result.AssistantText))
        {
            return Fail(providerName, "No assistant text was received alongside the tool call.");
        }

        return new ProviderConsoleScenarioResult(providerName, ScenarioName, true, 0);
    }

    private static ProviderConsoleScenarioResult Fail(string providerName, string errorMessage)
    {
        return new ProviderConsoleScenarioResult(providerName, ScenarioName, false, 0, ErrorMessage: errorMessage);
    }

    private static ProviderConsoleScenarioResult WithToolDetails(
        DshConsoleExecutionOptions executionOptions,
        ProviderConsoleScenarioResult outcome,
        DshScenarioExecutionResult result)
    {
        if (!executionOptions.Verbose)
        {
            return outcome;
        }

        var toolLines = result.ToolCalls
            .Select(toolCall => $"Tool: {toolCall.Id} / {toolCall.Name} / {toolCall.Status}");
        string[] identityLines = [$"Assistant message ids: {result.AssistantMessageIdCount}"];

        return outcome with { DetailLines = [.. outcome.DetailLines ?? [], .. toolLines, .. identityLines] };
    }
}
