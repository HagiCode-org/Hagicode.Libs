using HagiCode.Libs.ConsoleTesting;
using HagiCode.Libs.Providers;
using HagiCode.Libs.Providers.DeepAgents;

namespace HagiCode.Libs.DeepAgents.Console.Scenarios;

public static class ToolcallMixedTranscriptScenario
{
    private static readonly IReadOnlyList<string> ExpectedLifecycle =
    [
        "session.started",
        "assistant",
        "tool.call",
        "assistant",
        "tool.update",
        "assistant",
        "tool.completed",
        "assistant",
        "terminal.completed"
    ];

    public static ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>> Create(DeepAgentsConsoleExecutionOptions executionOptions)
    {
        ArgumentNullException.ThrowIfNull(executionOptions);

        return new ProviderConsoleScenario<ICliProvider<DeepAgentsOptions>>(
            "Toolcall Mixed Transcript",
            "Validate that assistant aggregation preserves interleaved toolcall lifecycle events.",
            (provider, cancellationToken) => ExecuteAsync(provider, executionOptions, cancellationToken));
    }

    private static async Task<ProviderConsoleScenarioResult> ExecuteAsync(
        ICliProvider<DeepAgentsOptions> provider,
        DeepAgentsConsoleExecutionOptions executionOptions,
        CancellationToken cancellationToken)
    {
        var options = executionOptions.CreateBaseOptions();
        const string prompt = "DeepAgents toolcall diagnostics: mixed transcript fixture.";
        var trace = await DeepAgentsToolcallTraceReader.ReadTraceAsync(provider, options, prompt, cancellationToken);

        var lifecycleMismatch = DeepAgentsToolcallTraceReader.FindFirstLifecycleMismatch(ExpectedLifecycle, trace.LifecycleStages);
        if (lifecycleMismatch is not null)
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected the mixed transcript fixture to preserve assistant and tool lifecycle boundaries.",
                lifecycleMismatch);
        }

        if (trace.AssistantMessages.Count < 4)
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected multiple assistant fragments around the toolcall lifecycle.",
                $"FirstMismatch: expected at least 4 assistant fragments but got {trace.AssistantMessages.Count}.");
        }

        if (trace.ToolEvents.Count < 3)
        {
            return CreateFailureResult(
                provider.Name,
                executionOptions,
                options,
                prompt,
                trace,
                "Expected the mixed transcript fixture to retain tool lifecycle nodes.",
                $"FirstMismatch: expected at least 3 tool events but got {trace.ToolEvents.Count}.");
        }

        var expectedAssistantFragments = new[]
        {
            "preparing tool call",
            "tool is still running",
            "tool output received",
            "assistant wrap-up after tool completed"
        };

        foreach (var expectedFragment in expectedAssistantFragments)
        {
            if (!trace.AssistantText.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase))
            {
                return CreateFailureResult(
                    provider.Name,
                    executionOptions,
                    options,
                    prompt,
                    trace,
                    "Assistant aggregation dropped or rewrote one of the mixed transcript fragments.",
                    $"FirstMismatch: assistant output did not contain '{expectedFragment}'.");
            }
        }

        var detailLines = DeepAgentsToolcallTraceReader.BuildDetailLines(
            executionOptions,
            options,
            "Toolcall Mixed Transcript",
            prompt,
            trace);
        return new ProviderConsoleScenarioResult(provider.Name, "Toolcall Mixed Transcript", true, 0, DetailLines: detailLines);
    }

    private static ProviderConsoleScenarioResult CreateFailureResult(
        string providerName,
        DeepAgentsConsoleExecutionOptions executionOptions,
        DeepAgentsOptions options,
        string prompt,
        DeepAgentsToolcallTrace trace,
        string errorMessage,
        string diagnosticLine)
    {
        var detailLines = DeepAgentsToolcallTraceReader.BuildDetailLines(
            executionOptions,
            options,
            "Toolcall Mixed Transcript",
            prompt,
            trace,
            [diagnosticLine]);
        return new ProviderConsoleScenarioResult(providerName, "Toolcall Mixed Transcript", false, 0, ErrorMessage: errorMessage, DetailLines: detailLines);
    }
}
